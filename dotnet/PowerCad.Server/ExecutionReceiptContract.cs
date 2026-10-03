using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PowerCad.Server;

/// <summary>
/// Read-only normalization of persisted plan state into an execution-evidence receipt.
///
/// This projector never changes CAD or plan state. It is deliberately conservative:
/// when the persisted evidence cannot prove either "nothing mutated" or "rollback was
/// verified", a failed/interrupted execution is INDETERMINATE and may not be replayed.
/// </summary>
public static class ExecutionReceiptContract
{
    public const string Schema = "power-cad-execution-receipt/1";

    private static string RequireText(JsonObject value, string name)
    {
        var text = value[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException($"Execution receipt requires {name}.");
        return text;
    }

    private static string? OptionalText(JsonObject value, string name)
        => value[name] is JsonValue node && node.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static bool? OptionalBool(JsonObject value, string name)
        => value[name] is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : null;

    private static string Digest(JsonObject value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToJsonString(CadJson.Options));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(ch =>
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));

    private static void CopySourceBinding(
        JsonObject receipt,
        JsonObject sourceBinding,
        JsonObject? committedResult,
        List<string> reasons)
    {
        var fields = new[]
        {
            "handoff_digest",
            "source_id",
            "source_byte_revision_id",
            "parser_revision_id",
        };

        foreach (var field in fields)
        {
            var value = OptionalText(sourceBinding, field);
            if (value is null)
            {
                reasons.Add($"source_binding_missing:{field}");
                continue;
            }

            var receiptField = field == "handoff_digest" ? "source_binding_handoff_digest" : field;
            receipt[receiptField] = value;

            if (committedResult is not null)
            {
                var observed = OptionalText(committedResult, receiptField);
                if (!StringComparer.Ordinal.Equals(value, observed))
                    reasons.Add($"committed_result_source_mismatch:{receiptField}");
            }
        }

        var handoffDigest = OptionalText(receipt, "source_binding_handoff_digest");
        if (handoffDigest is not null && !IsSha256(handoffDigest))
            reasons.Add("invalid_source_binding_handoff_digest");
    }

    /// <summary>
    /// Normalize an existing persisted plan into an immutable evidence receipt.
    ///
    /// Terminal plan states are Committed, Failed and Indeterminate. A persisted
    /// Executing state is treated as INDETERMINATE because the caller cannot know
    /// whether the host committed before communication was lost. Prepared/Previewed
    /// plans have not attempted execution and therefore do not produce receipts.
    /// </summary>
    public static JsonObject Project(JsonObject plan)
    {
        var state = RequireText(plan, "state");
        if (state is "Prepared" or "Previewed")
            throw new InvalidOperationException("Prepared/Previewed plans have no execution receipt.");
        if (state is not ("Executing" or "Committed" or "Failed" or "Indeterminate"))
            throw new InvalidOperationException($"Unsupported plan state for execution receipt: {state}.");

        var planId = RequireText(plan, "plan_id");
        var documentId = RequireText(plan, "document_id");
        var reasons = new List<string>();

        var status = "INDETERMINATE";
        var planTerminal = state is not "Executing";
        var committed = false;
        var rollbackVerified = OptionalBool(plan, "rollback_verified") is true;
        var mutationStarted = OptionalBool(plan, "mutation_started");

        JsonObject? result = plan["result"] as JsonObject;

        if (state == "Committed")
        {
            if (result is null
                || OptionalBool(result, "committed") is not true
                || OptionalBool(result, "dry_run") is not false)
            {
                reasons.Add("committed_plan_missing_verifiable_result");
            }
            else
            {
                status = "COMMITTED";
                committed = true;
            }
        }
        else if (state == "Failed")
        {
            if (rollbackVerified)
            {
                status = "ROLLED_BACK";
            }
            else if (mutationStarted is false)
            {
                status = "REJECTED";
            }
            else
            {
                reasons.Add("failed_plan_lacks_rollback_or_no-mutation_proof");
            }
        }
        else if (state == "Indeterminate")
        {
            reasons.Add("plan_state_indeterminate");
        }
        else // Executing persisted/read outside the active execution call
        {
            reasons.Add("plan_state_executing_outcome_unknown");
        }

        var receipt = new JsonObject
        {
            ["schema"] = Schema,
            ["receipt_status"] = status,
            ["plan_state"] = state,
            ["plan_terminal"] = planTerminal,
            ["executor"] = "power-cad",
            ["plan_id"] = planId,
            ["document_id"] = documentId,
            ["committed"] = committed,
            ["rollback_verified"] = rollbackVerified,
            ["mutation_started"] = mutationStarted,
            ["error_code"] = OptionalText(plan, "error_code"),
            ["auto_retry_allowed"] = false,
            ["requires_manual_reconciliation"] = status == "INDETERMINATE",
            ["safe_to_create_replacement_plan"] = status is "ROLLED_BACK" or "REJECTED",
            ["canonical_mutation"] = false,
            ["evidence_only"] = true,
        };

        if (plan["source_binding"] is JsonObject sourceBinding)
            CopySourceBinding(receipt, sourceBinding, status == "COMMITTED" ? result : null, reasons);

        if (result is not null)
            receipt["executor_result_digest"] = Digest(result);

        // If a plan looked committed but source-linked evidence is inconsistent,
        // downgrade it rather than emitting a false COMMITTED receipt.
        if (status == "COMMITTED" && reasons.Count > 0)
        {
            status = "INDETERMINATE";
            receipt["receipt_status"] = status;
            receipt["committed"] = false;
            receipt["requires_manual_reconciliation"] = true;
            receipt["safe_to_create_replacement_plan"] = false;
        }

        receipt["reasons"] = new JsonArray(reasons.OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => JsonValue.Create(x)).ToArray());

        var digestPayload = receipt.DeepClone().AsObject();
        receipt["receipt_digest"] = Digest(digestPayload);
        return receipt;
    }
}
