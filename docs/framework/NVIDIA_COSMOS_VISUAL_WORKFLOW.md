# NVIDIA Cosmos visual verification

Power CAD can use NVIDIA Cosmos Reason as a **read-only secondary verifier** after the deterministic AutoCAD transaction/read-back path.

## Configuration

Set secrets outside Git:

```powershell
$env:NVIDIA_API_KEY = "nvapi-..."
$env:NVIDIA_COSMOS_ENDPOINT = "https://integrate.api.nvidia.com/v1/chat/completions"
# Hosted Build currently exposes Reason2 8B publicly; omit MODEL for auto-selection.
# For a self-hosted Reason2 2B NIM:
$env:NVIDIA_COSMOS_MODEL = "nvidia/cosmos-reason2-2b"
```

For a self-hosted NIM, point `NVIDIA_COSMOS_ENDPOINT` to its `/v1/chat/completions` endpoint. The model name is configurable because hosted catalog availability can differ from self-hosted NIM availability.

## Tools

- `cad_visual_inspect`: identify visible CAD objects and anomalies.
- `cad_visual_verify`: check whether an expected post-edit state is visibly plausible.

Both tools capture the current AutoCAD view through the existing snapshot command and send only that raster image to the configured NVIDIA endpoint.

## Authority boundary

The visual model is never authoritative:

```text
cad_plan_execute
  -> deterministic entity read-back
  -> cad_visual_verify
  -> visual_advisory evidence
  -> user/agent review
```

Vision output does not unlock layers, approve a write, change CAIR/Ontology truth, or bypass fingerprint checks. Model `<think>` traces are discarded; only final structured evidence is returned.

## Hosted vs 2B NIM

The adapter chooses `nvidia/cosmos-reason2-8b` by default when
`NVIDIA_COSMOS_ENDPOINT` points to NVIDIA Build (`integrate.api.nvidia.com`).
For a self-hosted NIM it defaults to `nvidia/cosmos-reason2-2b`.
`NVIDIA_COSMOS_MODEL` always overrides the automatic choice.
