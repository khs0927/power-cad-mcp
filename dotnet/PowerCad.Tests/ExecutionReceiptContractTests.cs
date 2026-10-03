using System.Text.Json.Nodes;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class ExecutionReceiptContractTests
{
    private static string H(char ch) => new(ch, 64);

    private static JsonObject SourceBinding() => new()
    {
        ["handoff_digest"] = H('d'),
        ["source_id"] = H('a'),
        ["source_byte_revision_id"] = H('b'),
        ["parser_revision_id"] = H('c'),
    };

    private static JsonObject CommittedResult() => new()
    {
        ["dry_run"] = false,
        ["committed"] = true,
        ["executor"] = "power-cad",
        ["plan_id"] = "plan-1",
        ["document_id"] = "doc-1",
        ["source_binding_handoff_digest"] = H('d'),
        ["source_id"] = H('a'),
        ["source_byte_revision_id"] = H('b'),
        ["parser_revision_id"] = H('c'),
        ["changes"] = new JsonArray(),
    };

    private static JsonObject Plan(string state) => new()
    {
        ["plan_id"] = "plan-1",
        ["document_id"] = "doc-1",
        ["state"] = state,
        ["created_at"] = "2026-10-03T00:00:00Z",
        ["source_binding"] = SourceBinding(),
    };

    [Fact]
    public void CommittedPlanProducesCommittedEvidenceOnlyWhenResultIsVerifiable()
    {
        var plan = Plan("Committed");
        plan["result"] = CommittedResult();

        var receipt = ExecutionReceiptContract.Project(plan);

        Assert.Equal("COMMITTED", receipt["receipt_status"]!.GetValue<string>());
        Assert.True(receipt["committed"]!.GetValue<bool>());
        Assert.False(receipt["auto_retry_allowed"]!.GetValue<bool>());
        Assert.False(receipt["requires_manual_reconciliation"]!.GetValue<bool>());
        Assert.False(receipt["canonical_mutation"]!.GetValue<bool>());
        Assert.True(receipt["evidence_only"]!.GetValue<bool>());
        Assert.Equal(H('d'), receipt["source_binding_handoff_digest"]!.GetValue<string>());
        Assert.Equal(64, receipt["receipt_digest"]!.GetValue<string>().Length);
    }

    [Fact]
    public void CommittedPlanWithMismatchedSourceEchoIsIndeterminate()
    {
        var plan = Plan("Committed");
        var result = CommittedResult();
        result["source_id"] = H('f');
        plan["result"] = result;

        var receipt = ExecutionReceiptContract.Project(plan);

        Assert.Equal("INDETERMINATE", receipt["receipt_status"]!.GetValue<string>());
        Assert.False(receipt["committed"]!.GetValue<bool>());
        Assert.True(receipt["requires_manual_reconciliation"]!.GetValue<bool>());
        Assert.Contains(
            "committed_result_source_mismatch:source_id",
            receipt["reasons"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Theory]
    [InlineData("executor", "other-executor", "committed_result_executor_mismatch")]
    [InlineData("plan_id", "other-plan", "committed_result_plan_id_mismatch")]
    [InlineData("document_id", "other-doc", "committed_result_document_id_mismatch")]
    public void CommittedResultMustBelongToTheSameExecutorPlanAndDocument(
        string field,
        string value,
        string reason)
    {
        var plan = Plan("Committed");
        var result = CommittedResult();
        result[field] = value;
        plan["result"] = result;

        var receipt = ExecutionReceiptContract.Project(plan);

        Assert.Equal("INDETERMINATE", receipt["receipt_status"]!.GetValue<string>());
        Assert.False(receipt["committed"]!.GetValue<bool>());
        Assert.True(receipt["requires_manual_reconciliation"]!.GetValue<bool>());
        Assert.Contains(reason, receipt["reasons"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void ContradictoryFailureEvidenceIsIndeterminate()
    {
        var plan = Plan("Failed");
        plan["rollback_verified"] = true;
        plan["mutation_started"] = false;

        var receipt = ExecutionReceiptContract.Project(plan);

        Assert.Equal("INDETERMINATE", receipt["receipt_status"]!.GetValue<string>());
        Assert.False(receipt["safe_to_create_replacement_plan"]!.GetValue<bool>());
        Assert.True(receipt["requires_manual_reconciliation"]!.GetValue<bool>());
        Assert.Contains(
            "conflicting_failure_evidence",
            receipt["reasons"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void FailedPlanIsRolledBackOnlyWithExplicitRollbackProof()
    {
        var plan = Plan("Failed");
        plan["rollback_verified"] = true;
        plan["mutation_started"] = true;
        plan["error_code"] = "POSTCONDITION_FAILED";

        var receipt = ExecutionReceiptContract.Project(plan);

        Assert.Equal("ROLLED_BACK", receipt["receipt_status"]!.GetValue<string>());
        Assert.True(receipt["rollback_verified"]!.GetValue<bool>());
        Assert.True(receipt["safe_to_create_replacement_plan"]!.GetValue<bool>());
        Assert.False(receipt["requires_manual_reconciliation"]!.GetValue<bool>());
    }

    [Fact]
    public void FailedPlanIsRejectedOnlyWithExplicitNoMutationProof()
    {
        var plan = Plan("Failed");
        plan["mutation_started"] = false;
        plan["error_code"] = "DOCUMENT_CHANGED";

        var receipt = ExecutionReceiptContract.Project(plan);

        Assert.Equal("REJECTED", receipt["receipt_status"]!.GetValue<string>());
        Assert.False(receipt["rollback_verified"]!.GetValue<bool>());
        Assert.True(receipt["safe_to_create_replacement_plan"]!.GetValue<bool>());
        Assert.False(receipt["auto_retry_allowed"]!.GetValue<bool>());
    }

    [Fact]
    public void UnprovenFailureIsIndeterminateAndNeverAutoRetried()
    {
        var plan = Plan("Failed");
        plan["error_code"] = "EXECUTION_UNCERTAIN";

        var receipt = ExecutionReceiptContract.Project(plan);

        Assert.Equal("INDETERMINATE", receipt["receipt_status"]!.GetValue<string>());
        Assert.True(receipt["requires_manual_reconciliation"]!.GetValue<bool>());
        Assert.False(receipt["safe_to_create_replacement_plan"]!.GetValue<bool>());
        Assert.False(receipt["auto_retry_allowed"]!.GetValue<bool>());
        Assert.Contains(
            "failed_plan_lacks_rollback_or_no-mutation_proof",
            receipt["reasons"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Theory]
    [InlineData("Indeterminate", true)]
    [InlineData("Executing", false)]
    public void InterruptedOrPersistedExecutingPlanIsIndeterminate(string state, bool terminal)
    {
        var receipt = ExecutionReceiptContract.Project(Plan(state));

        Assert.Equal("INDETERMINATE", receipt["receipt_status"]!.GetValue<string>());
        Assert.Equal(terminal, receipt["plan_terminal"]!.GetValue<bool>());
        Assert.False(receipt["auto_retry_allowed"]!.GetValue<bool>());
        Assert.True(receipt["requires_manual_reconciliation"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("Previewed")]
    public void NonExecutedPlanDoesNotProduceReceipt(string state)
    {
        Assert.Throws<InvalidOperationException>(() => ExecutionReceiptContract.Project(Plan(state)));
    }

    [Fact]
    public void MissingCommittedResultIsIndeterminate()
    {
        var receipt = ExecutionReceiptContract.Project(Plan("Committed"));

        Assert.Equal("INDETERMINATE", receipt["receipt_status"]!.GetValue<string>());
        Assert.Contains(
            "committed_plan_missing_verifiable_result",
            receipt["reasons"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void ReceiptDigestIsDeterministicAndChangesWithEvidence()
    {
        var plan = Plan("Committed");
        plan["result"] = CommittedResult();

        var first = ExecutionReceiptContract.Project(plan);
        var second = ExecutionReceiptContract.Project(plan);
        Assert.Equal(
            first["receipt_digest"]!.GetValue<string>(),
            second["receipt_digest"]!.GetValue<string>());

        plan["result"]!["changes"] = new JsonArray(new JsonObject { ["handle"] = "2F3" });
        var changed = ExecutionReceiptContract.Project(plan);
        Assert.NotEqual(
            first["receipt_digest"]!.GetValue<string>(),
            changed["receipt_digest"]!.GetValue<string>());
    }
}
