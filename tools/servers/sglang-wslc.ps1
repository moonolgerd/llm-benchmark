# Serve gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090 with SGLang in a WSLc container on :30000.
# Flags follow the model card (https://huggingface.co/gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090).
#
# Two profiles, per the card (one 32 GB card gets one or the other, not both):
#   default          DSpark speculative decoding: ~130-160 tok/s decode, KV pool ~141k tokens
#                    (observed max_total_num_tokens=141439; set the client context cap to ~141k)
#   -NoSpeculation   max-context profile: ~80 tok/s decode, KV pool ~290k tokens, full 262k window
#
# The HF cache is bind-mounted from $HfCache so model weights (~20 GB) stay off C:.
# WSLc keeps its own image/storage disk on C: (image is ~37 GB).
param(
    [switch]$NoSpeculation,
    [string]$HfCache = "E:\models\hf",
    [string]$Name = "sgl"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $HfCache | Out-Null

# Recreate: container arguments are fixed at creation time.
if (wslc container ls -a 2>&1 | Select-String -Pattern " $Name\s*$" -Quiet) {
    wslc stop $Name | Out-Null
    wslc rm $Name | Out-Null
}

$serve = @(
    "--model-path", "gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090",
    "--trust-remote-code", "--tp-size", "1",
    "--context-length", "262144",
    "--kv-cache-dtype", "fp8_e4m3",
    "--attention-backend", "flashinfer",
    "--chunked-prefill-size", "2048",
    "--mamba-radix-cache-strategy", "extra_buffer_lazy",
    "--mamba-ssm-dtype", "bfloat16",
    "--mem-fraction-static", "0.90",
    "--max-running-requests", "2",
    "--max-mamba-cache-size", "12"
)
if (-not $NoSpeculation) {
    $serve += @(
        "--speculative-algorithm", "DSPARK",
        "--speculative-draft-model-path", "gittensor-model-hub/Qwen3.8-27B-DSpark-NVFP4",
        "--speculative-dspark-block-size", "7",
        "--speculative-draft-model-quantization", "modelopt_fp4"
    )
}
$serve += @(
    "--reasoning-parser", "qwen3",
    "--tool-call-parser", "qwen3_coder",
    "--host", "0.0.0.0", "--port", "30000"
)

# --ipc=host is omitted (single GPU, tp-size 1); --shm-size covers shared memory.
wslc run -d --name $Name --gpus all --shm-size 32g -p 30000:30000 `
    -v "${HfCache}:/root/.cache/huggingface" `
    lmsysorg/sglang:latest sglang serve @serve

# Follow startup with:  wslc logs $Name
# Ready when:           curl http://127.0.0.1:30000/v1/models
