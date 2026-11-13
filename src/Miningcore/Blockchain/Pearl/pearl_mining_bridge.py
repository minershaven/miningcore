#!/usr/bin/env python3
"""
pearl_mining_bridge.py
======================
Sidecar bridging Miningcore (C#) to the Pearl mining stack. It reuses the
*upstream* pearl-gateway serialization code so produced blocks are byte-identical
to what pearl-gateway would submit.

Requires (installed in the same Python env):
  - pearl_mining            (py-pearl-mining: IncompleteBlockHeader, PlainProof,
                             ZKProof, generate_proof_for_cert_version, verify_plain_proof_for_cert_version)
  - pearl_gateway           (miner/pearl-gateway: BlockTemplate, PearlBlock,
                             PearlHeader, ZKCertificate, ProofGenerator)

Protocol: line-delimited JSON-RPC on stdin/stdout.

  -> { "method": "set_template", "id", "jobId", "miningAddress", "template": {...} }
  <- { "id", "incompleteHeaderHex", "target" }

  -> { "method": "submit_proof", "id", "jobId", "plainProofB64" }
  <- { "id", "valid", "meetsTarget", "shareDifficulty", "networkDifficulty",
       "blockHex", "blockHash", "error" }

  startup: { "ready": "ok" }  (or { "ready": "error", "detail": ... })

The `template` object is the raw getblocktemplate result (snake/camel fields as
returned by pearld); we feed it straight into the gateway's
BlockTemplate.from_get_block_template via GetBlockTemplateResponse.
"""

import os
import sys
import time
import json
import base64
import gzip
import zlib
import traceback

try:
    from pearl_mining import (
        IncompleteBlockHeader,
        PlainProof,
        generate_proof_for_cert_version,
        verify_plain_proof_for_cert_version,
    )
    from pearl_gateway.comm.dataclasses import BlockTemplate
    from pearl_gateway.proof_generator import ProofGenerator
    from pearl_gateway.rpc_types import GetBlockTemplateResponse
    from pearl_gateway.blockchain_utils.blockchain_utils import bits_to_target
except Exception as e:  # noqa: BLE001
    sys.stdout.write(json.dumps({"ready": "error", "detail": repr(e)}) + "\n")
    sys.stdout.flush()
    sys.exit(1)


# Difficulty-1 target (Bitcoin convention), used to convert a hash/target into a
# human "difficulty" number for share accounting.
DIFF1_TARGET = 0x00000000FFFF0000000000000000000000000000000000000000000000000000


# jobId -> (BlockTemplate, network_target_int)
TEMPLATES: dict[str, tuple] = {}


def difficulty_from_target(target: int) -> float:
    if target <= 0:
        return 0.0
    return DIFF1_TARGET / target


def handle_set_template(req: dict) -> dict:
    job_id = req["jobId"]
    mining_address = req["miningAddress"]
    raw = req["template"]

    # pearld returns standard GBT JSON; validate via the gateway's model.
    gbt = GetBlockTemplateResponse.model_validate(raw)
    template = BlockTemplate.from_get_block_template(gbt, mining_address=mining_address)

    network_target = template.target  # int
    TEMPLATES[job_id] = (template, network_target)
    while len(TEMPLATES) > 16:  # keep only recent jobs (dicts preserve insertion order)
        TEMPLATES.pop(next(iter(TEMPLATES)))

    incomplete_header_bytes = template.header.serialize_without_proof_commitment()

    return {
        "incompleteHeaderHex": incomplete_header_bytes.hex(),
        "target": hex(network_target),
    }


MAX_PROOF_BYTES = 6 * 1024 * 1024  # decompressed cap (Pearl stratum spec v1.5)


def _inflate(raw: bytes) -> bytes:
    """Auto-detect gzip (RFC 1952) or zlib (RFC 1950) compressed proofs."""
    if raw[:2] == b"\x1f\x8b":
        d = zlib.decompressobj(16 + zlib.MAX_WBITS)
    elif len(raw) >= 2 and (raw[0] & 0x0F) == 8 and ((raw[0] << 8) | raw[1]) % 31 == 0:
        d = zlib.decompressobj()
    else:
        return raw
    try:
        out = d.decompress(raw, MAX_PROOF_BYTES + 1)
    except zlib.error:
        return raw  # not actually compressed; treat as raw bincode
    if len(out) > MAX_PROOF_BYTES:
        raise ValueError("proof too large after decompression")
    return out


def _deserialize_plain_proof(payload: str) -> PlainProof:
    """
    Reconstruct a PlainProof from the miner-submitted payload.

    Wire format (Pearl stratum v1, as sent by SRBMiner-MULTI and others):
    base64 of the bincode PlainProof bytes, raw or gzip/zlib-compressed
    (auto-detected). A hex string of the raw bytes is also accepted.
    """
    payload = payload.strip()

    is_hex = (
        len(payload) >= 2
        and len(payload) % 2 == 0
        and all(c in "0123456789abcdefABCDEF" for c in payload)
    )
    if is_hex:
        raw = bytes.fromhex(payload)
    else:
        payload = payload.replace("-", "+").replace("_", "/")  # tolerate base64url
        payload += "=" * (-len(payload) % 4)                   # tolerate missing padding
        raw = base64.b64decode(payload, validate=False)
    raw = _inflate(raw)
    if len(raw) > MAX_PROOF_BYTES:
        raise ValueError("proof too large")

    return PlainProof.from_base64(base64.b64encode(raw).decode("ascii"))


def target_to_bits(target: int) -> int:
    """Encode a 256-bit target into Bitcoin compact 'bits' format."""
    if target <= 0:
        return 0
    size = (target.bit_length() + 7) // 8
    if size <= 3:
        compact = target << (8 * (3 - size))
    else:
        compact = target >> (8 * (size - 3))
    # Avoid the sign bit in the mantissa.
    if compact & 0x00800000:
        compact >>= 8
        size += 1
    return (size << 24) | (compact & 0x007FFFFF)


def share_nbits_target(nbits: int) -> int:
    return bits_to_target(nbits)


def difficulty_to_share_nbits(diff: float) -> int:
    """Share target = DIFF1 / difficulty, encoded as compact bits."""
    if diff <= 0:
        diff = 1.0
    scale = 1 << 16
    share_target = (DIFF1_TARGET * scale) // int(diff * scale)
    return target_to_bits(share_target)


BAD_PROOF_DIR = os.environ.get("PEARL_BAD_PROOF_DIR", "/opt/miningcore/pearl/bad-proofs")


def _keep_bad_proof(payload: str, err: Exception) -> None:
    """Keep a few undecodable submissions for offline diagnosis."""
    try:
        os.makedirs(BAD_PROOF_DIR, exist_ok=True)
        if len(os.listdir(BAD_PROOF_DIR)) >= 20:
            return
        name = os.path.join(BAD_PROOF_DIR, f"{int(time.time() * 1000)}.txt")
        with open(name, "w") as f:
            f.write(f"# {type(err).__name__}: {err}\n# len={len(payload)} mod4={len(payload) % 4}\n")
            f.write(payload)
    except Exception:  # noqa: BLE001
        pass


def handle_submit_proof(req: dict) -> dict:
    job_id = req["jobId"]
    if job_id not in TEMPLATES:
        return {"valid": False, "error": "stale job", "meetsTarget": False}

    template, network_target = TEMPLATES[job_id]
    incomplete_header = template.header.incomplete_header
    # The certificate version is dictated by the block height via the
    # template (requiredcertversion from getblocktemplate).
    cert_version = int(template.required_cert_version)

    try:
        plain_proof = _deserialize_plain_proof(req["plainProofB64"])
    except Exception as e:  # noqa: BLE001
        _keep_bad_proof(req["plainProofB64"], e)
        return {"valid": False, "error": f"bad proof data: {e}", "meetsTarget": False}

    network_difficulty = difficulty_from_target(network_target)
    worker_diff = float(req.get("difficulty", 1.0)) or 1.0

    # 1. Validate as a SHARE: verify against the worker's (lower) share target
    #    via nbits_override, so pool-difficulty solutions are accepted even when
    #    they do not meet the full network target.
    share_nbits = difficulty_to_share_nbits(worker_diff)
    try:
        ok, msg = verify_plain_proof_for_cert_version(cert_version, incomplete_header, plain_proof, share_nbits)
    except Exception as e:  # noqa: BLE001  (e.g. proof not eligible for this cert version)
        return {"valid": False, "error": f"invalid proof: {e}", "meetsTarget": False}
    if not ok:
        return {"valid": False, "error": f"invalid proof: {msg}", "meetsTarget": False}

    # 2. Check BLOCK candidacy: verify against the header's own network nbits.
    # Skip the second verification when the share target is already at least
    # as hard as the network target.
    if share_nbits_target(share_nbits) <= network_target:
        meets_target = True
    else:
        meets_target, _ = verify_plain_proof_for_cert_version(cert_version, incomplete_header, plain_proof)

    block_hex = None
    block_hash = None
    error = None

    if meets_target:
        try:
            block = ProofGenerator.generate_block(plain_proof, template, debug_mode=False)
            block_hex = block.serialize().hex()
            block_hash = block.zk_certificate.header_hash[::-1].hex()
        except Exception as e:  # noqa: BLE001
            meets_target = False
            error = f"generate_block: {e}"

    return {
        "valid": True,
        "meetsTarget": meets_target,
        "shareDifficulty": worker_diff,
        "networkDifficulty": network_difficulty,
        "blockHex": block_hex,
        "blockHash": block_hash,
        "error": error,
    }


HANDLERS = {
    "set_template": handle_set_template,
    "submit_proof": handle_submit_proof,
}


def main() -> None:
    sys.stdout.write(json.dumps({"ready": "ok"}) + "\n")
    sys.stdout.flush()

    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue

        req_id = None
        try:
            req = json.loads(raw)
            req_id = req.get("id")
            method = req["method"]
            handler = HANDLERS.get(method)
            if handler is None:
                raise ValueError(f"Unknown method: {method!r}")
            result = handler(req)
            result["id"] = req_id
            sys.stdout.write(json.dumps(result) + "\n")
        except Exception as exc:  # noqa: BLE001
            sys.stdout.write(json.dumps({
                "id": req_id,
                "error": f"{type(exc).__name__}: {exc}",
                "trace": traceback.format_exc(),
            }) + "\n")

        sys.stdout.flush()


if __name__ == "__main__":
    main()
