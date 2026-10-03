# NVIDIA Cosmos Reason2 visual verification

Power CAD uses NVIDIA Cosmos Reason2 2B as a **read-only secondary verifier** after the deterministic AutoCAD transaction/read-back path.

## Official Reason2 2B deployment

The current NVIDIA NIM documentation runs `nvidia/cosmos-reason2-2b` behind an OpenAI-compatible local NIM endpoint:

```text
http://127.0.0.1:8000/v1/chat/completions
```

Pulling/launching the NIM container uses `NGC_API_KEY`. Querying a local NIM does not require bearer authentication. If you expose a private remote NIM behind an authenticated gateway, set `NVIDIA_API_KEY` for that gateway.

```powershell
$env:NVIDIA_COSMOS_ENDPOINT = "http://127.0.0.1:8000/v1/chat/completions"
$env:NVIDIA_COSMOS_MODEL = "nvidia/cosmos-reason2-2b"
# Optional only for a remote endpoint that requires bearer auth:
$env:NVIDIA_API_KEY = "..."
```

The endpoint remains configurable so the same contract can target a remote/self-hosted NIM.

## Tools

- `cad_visual_inspect`: identify visible CAD objects and anomalies.
- `cad_visual_verify`: check whether an expected post-edit state is visibly plausible.

Both tools capture the current AutoCAD view through the existing snapshot command and send only that raster image to the configured Reason2 endpoint.

## Authority boundary

```text
cad_plan_execute
  -> deterministic entity read-back
  -> cad_visual_verify
  -> visual_advisory evidence
  -> user/agent review
```

Vision output does not unlock layers, approve a write, change Ontology truth, or bypass fingerprint checks. Model `<think>` traces are discarded; only final structured evidence is returned.
