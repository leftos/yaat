# /// script
# requires-python = ">=3.13"
# dependencies = ["boto3>=1.43.123"]
# ///
"""Publish the sherpa natives and STT models to Cloudflare R2 and rewrite the client's download manifest.

Hashes every local file, uploads it to the `yaat-downloads` bucket through R2's S3-compatible API (multipart for
large files), checks each object's public `Content-Length` at https://dl.leftos.dev/, and writes
src/Yaat.Client/Speech/stt-downloads.json last, so a failed run leaves the old manifest in place.

Credentials (R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY) are read from
G:\\My Drive\\Private Config\\yaat\\secrets\\r2.env; no value is ever printed.

Usage:
    uv run tools/publish_stt_downloads.py --natives-dir .tmp/natives-<sha> --native-commit <40-hex> \\
        --model fp32=<dir> --model int8=<dir> --license <file> --notice <file> [--dry-run]
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import boto3
from boto3.s3.transfer import TransferConfig
from botocore.exceptions import ClientError

BASE_URL = "https://dl.leftos.dev/"
BUCKET = "yaat-downloads"
REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MANIFEST = REPO_ROOT / "src" / "Yaat.Client" / "Speech" / "stt-downloads.json"
SECRETS_PATH = Path("G:/My Drive/Private Config/yaat/secrets/r2.env")
SECRET_KEYS = ("R2_ACCOUNT_ID", "R2_ACCESS_KEY_ID", "R2_SECRET_ACCESS_KEY")
EXIT_FAILED = 1
EXIT_BAD_INPUT = 2
MULTIPART_BYTES = 64 * 1024 * 1024

NATIVE_ZIPS: dict[str, tuple[str, str]] = {
    "linux-x64-cpu.zip": ("linux-x64", "cpu"),
    "linux-x64-cuda.zip": ("linux-x64", "cuda"),
    "osx-arm64.zip": ("osx-arm64", "cpu"),
    "osx-x64.zip": ("osx-x64", "cpu"),
    "win-x64-cpu.zip": ("win-x64", "cpu"),
    "win-x64-cuda.zip": ("win-x64", "cuda"),
}

MODEL_NAME = "rasr-parakeet-v2"
MODEL_FILES: dict[str, tuple[str, ...]] = {
    "fp32": ("encoder.onnx", "encoder.weights", "decoder.onnx", "joiner.onnx", "tokens.txt", "bpe.vocab"),
    "int8": ("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt", "bpe.vocab"),
}
TEXT_NAMES = ("LICENSE", "NOTICE")


class PublishError(Exception):
    """A failure the tool reports as one message and an exit code."""

    def __init__(self, message: str, exit_code: int) -> None:
        super().__init__(message)
        self.exit_code = exit_code


@dataclass(frozen=True)
class LocalFile:
    """One local file and the bucket key it publishes to."""

    key: str
    path: Path


@dataclass(frozen=True)
class HashedFile:
    """A local file with its size and SHA-256, ready to upload."""

    key: str
    path: Path
    size: int
    sha256: str


@dataclass(frozen=True)
class NativeZip:
    """One sherpa native zip for a runtime identifier and variant."""

    rid: str
    variant: str
    file: LocalFile


@dataclass(frozen=True)
class Model:
    """One model id and its files, keyed by file name."""

    model_id: str
    files: dict[str, LocalFile]


def parse_model_arg(value: str) -> tuple[str, Path]:
    """Parses `--model <kind>=<dir>`.

    Args:
        value: The argument text.

    Returns:
        The model kind and its folder.

    Raises:
        argparse.ArgumentTypeError: When the kind is unknown or the form is wrong.
    """
    kind, sep, folder = value.partition("=")
    if (not sep) or (kind not in MODEL_FILES) or (not folder):
        raise argparse.ArgumentTypeError(f"expected <kind>=<dir> with kind one of {', '.join(MODEL_FILES)}, got {value!r}")
    return kind, Path(folder)


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    """Parses the command line.

    Args:
        argv: Arguments without the program name, or None for sys.argv.

    Returns:
        The parsed arguments.
    """
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--natives-dir", type=Path, required=True, help="folder holding the six CI zips (<name>.zip/<name>.zip or <name>.zip)")
    parser.add_argument("--native-commit", required=True, help="40-hex sherpa-onnx fork commit the natives were built from")
    parser.add_argument("--model", type=parse_model_arg, action="append", required=True, help="fp32=<dir> or int8=<dir>, repeatable")
    parser.add_argument("--license", type=Path, required=True, help="licence file published as LICENSE with every model")
    parser.add_argument("--notice", type=Path, required=True, help="notice file published as NOTICE with every model")
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST, help="manifest to write (default: %(default)s)")
    parser.add_argument("--dry-run", action="store_true", help="hash and write the manifest, upload nothing")
    return parser.parse_args(argv)


def require_file(path: Path, what: str) -> Path:
    """Returns the path when it is a file.

    Args:
        path: The file expected to exist.
        what: What the file is, for the error message.

    Raises:
        PublishError: When the file does not exist.
    """
    if not path.is_file():
        raise PublishError(f"missing {what}: {path}", EXIT_BAD_INPUT)
    return path


def find_native_zip(natives_dir: Path, name: str) -> Path:
    """Finds a native zip laid out as `gh run download` writes it, or as a plain file.

    Args:
        natives_dir: The folder holding the zips.
        name: The zip's file name, such as `win-x64-cpu.zip`.

    Returns:
        The zip's path.

    Raises:
        PublishError: When neither layout has the zip.
    """
    candidate = natives_dir / name
    nested = candidate / name
    if nested.is_file():
        return nested
    return require_file(candidate, f"native zip {name}")


def collect_natives(natives_dir: Path, commit: str) -> list[NativeZip]:
    """Collects the six native zips, sorted by (rid, variant).

    Args:
        natives_dir: The folder holding the zips.
        commit: The 40-hex sherpa-onnx commit the zips were built from.

    Returns:
        The natives in manifest order.

    Raises:
        PublishError: When the commit is malformed or a zip is missing.
    """
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise PublishError(f"--native-commit must be 40 lowercase hex digits, got {commit!r}", EXIT_BAD_INPUT)
    natives = [
        NativeZip(rid, variant, LocalFile(f"sherpa-native/{commit[:10]}/{name}", find_native_zip(natives_dir, name)))
        for name, (rid, variant) in NATIVE_ZIPS.items()
    ]
    return sorted(natives, key=lambda native: (native.rid, native.variant))


def collect_models(model_args: list[tuple[str, Path]], license_file: Path, notice_file: Path) -> list[Model]:
    """Collects each requested model's files plus the licence and notice, fp32 first.

    Args:
        model_args: The parsed `--model` arguments.
        license_file: The licence published as LICENSE with every model.
        notice_file: The notice published as NOTICE with every model.

    Returns:
        The models in manifest order.

    Raises:
        PublishError: When a kind is given twice or a file is missing.
    """
    folders: dict[str, Path] = {}
    for kind, folder in model_args:
        if kind in folders:
            raise PublishError(f"--model {kind} given more than once", EXIT_BAD_INPUT)
        folders[kind] = folder
    shared = {"LICENSE": require_file(license_file, "licence file"), "NOTICE": require_file(notice_file, "notice file")}
    models = []
    for kind in (kind for kind in MODEL_FILES if kind in folders):
        prefix = f"stt/{MODEL_NAME}/{kind}"
        paths = {name: require_file(folders[kind] / name, f"{kind} model file {name}") for name in MODEL_FILES[kind]}
        paths.update(shared)
        files = {name: LocalFile(f"{prefix}/{name}", paths[name]) for name in sorted(paths)}
        models.append(Model(f"{MODEL_NAME}-{kind}", files))
    return models


def hash_file(local: LocalFile) -> HashedFile:
    """Hashes one file.

    Args:
        local: The file and its key.

    Returns:
        The file with its size and SHA-256.
    """
    print(f"hash {local.key}", flush=True)
    with local.path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return HashedFile(local.key, local.path, local.path.stat().st_size, digest)


def build_manifest(natives: list[NativeZip], commit: str, models: list[Model]) -> tuple[dict[str, Any], list[HashedFile]]:
    """Hashes every file and builds the manifest.

    Args:
        natives: The native zips in manifest order.
        commit: The sherpa-onnx commit the natives were built from.
        models: The models in manifest order.

    Returns:
        The manifest and every hashed file, in manifest order.
    """
    hashed: list[HashedFile] = []
    native_entries = []
    for native in natives:
        file = hash_file(native.file)
        hashed.append(file)
        native_entries.append({"rid": native.rid, "variant": native.variant, "key": file.key, "size": file.size, "sha256": file.sha256})
    model_entries = []
    for model in models:
        file_entries = []
        for name, local in model.files.items():
            file = hash_file(local)
            hashed.append(file)
            file_entries.append({"name": name, "key": file.key, "size": file.size, "sha256": file.sha256})
        model_entries.append({"id": model.model_id, "files": file_entries})
    manifest = {"baseUrl": BASE_URL, "natives": {"commit": commit, "files": native_entries}, "models": model_entries}
    return manifest, hashed


def load_r2_credentials(path: Path) -> dict[str, str]:
    """Reads the R2 credentials from a `KEY=VALUE` secrets file without printing any value.

    Args:
        path: The secrets file.

    Returns:
        The three R2 values by key name.

    Raises:
        PublishError: When the drive, the file or a key is missing.
    """
    if not Path(path.anchor).exists():
        raise PublishError(f"drive {path.anchor} is not available; the R2 secrets are expected at {path}", EXIT_BAD_INPUT)
    if not path.is_file():
        raise PublishError(f"R2 secrets file not found: {path}", EXIT_BAD_INPUT)
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        key, sep, value = line.partition("=")
        if sep and key.strip() in SECRET_KEYS and value.strip():
            values[key.strip()] = value.strip()
    missing = [key for key in SECRET_KEYS if key not in values]
    if missing:
        raise PublishError(f"{path} is missing {', '.join(missing)}", EXIT_BAD_INPUT)
    return values


def make_s3_client(credentials: dict[str, str]) -> Any:
    """Creates an S3 client for the account's R2 endpoint.

    Args:
        credentials: The values from load_r2_credentials.

    Returns:
        A boto3 S3 client.
    """
    return boto3.client(
        "s3",
        endpoint_url=f"https://{credentials['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        region_name="auto",
        aws_access_key_id=credentials["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=credentials["R2_SECRET_ACCESS_KEY"],
    )


def content_type(key: str) -> str:
    """Returns the Content-Type an object is uploaded with.

    Args:
        key: The object key.
    """
    name = key.rsplit("/", 1)[-1]
    if name.endswith(".zip"):
        return "application/zip"
    if name.endswith(".txt") or (name in TEXT_NAMES):
        return "text/plain; charset=utf-8"
    return "application/octet-stream"


def is_published(client: Any, file: HashedFile) -> bool:
    """Tells whether the bucket already holds this exact file.

    Args:
        client: The S3 client.
        file: The local file.

    Returns:
        True when the object's size and sha256 metadata match the local file.
    """
    try:
        head = client.head_object(Bucket=BUCKET, Key=file.key)
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") in ("404", "NoSuchKey", "NotFound"):
            return False
        raise
    return (head.get("ContentLength") == file.size) and (head.get("Metadata", {}).get("sha256") == file.sha256)


def upload_all(client: Any, files: list[HashedFile]) -> None:
    """Uploads every file the bucket does not already hold, skipping exact matches.

    Args:
        client: The S3 client.
        files: The hashed files.
    """
    transfer = TransferConfig(multipart_threshold=MULTIPART_BYTES, multipart_chunksize=MULTIPART_BYTES)
    for file in files:
        if is_published(client, file):
            print(f"skip {file.key}", flush=True)
            continue
        print(f"upload {file.key} ({file.size} bytes)", flush=True)
        extra = {"ContentType": content_type(file.key), "Metadata": {"sha256": file.sha256}}
        client.upload_file(str(file.path), BUCKET, file.key, ExtraArgs=extra, Config=transfer)


def public_content_length(url: str) -> str:
    """HEADs a public URL.

    Args:
        url: The URL.

    Returns:
        The Content-Length header, or a description of why there is none.
    """
    request = urllib.request.Request(url, method="HEAD", headers={"User-Agent": "yaat-publish-stt-downloads/1.0"})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.headers.get("Content-Length") or "no Content-Length"
    except urllib.error.HTTPError as exc:
        return f"HTTP {exc.code}"
    except urllib.error.URLError as exc:
        return f"network error {exc.reason}"


def verify_public(files: list[HashedFile]) -> None:
    """Checks that every object is served at its public URL with the local size.

    Args:
        files: The hashed files.

    Raises:
        PublishError: When any object's public Content-Length differs from the local size.
    """
    failures = []
    for file in files:
        url = BASE_URL + file.key
        reported = public_content_length(url)
        if reported != str(file.size):
            failures.append(f"{url}: expected {file.size} bytes, got {reported}")
    if failures:
        raise PublishError("public check failed:\n  " + "\n  ".join(failures), EXIT_FAILED)
    print(f"verified {len(files)} public objects", flush=True)


def write_manifest(path: Path, manifest: dict[str, Any]) -> None:
    """Writes the manifest LF-terminated with 2-space indents, replacing the old one in one step.

    Args:
        path: The manifest path.
        manifest: The manifest.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_name(path.name + ".tmp")
    with staging.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(manifest, indent=2) + "\n")
    staging.replace(path)
    print(f"wrote {path}", flush=True)


def run(args: argparse.Namespace) -> None:
    """Collects, hashes, uploads, verifies and writes the manifest.

    Args:
        args: The parsed arguments.
    """
    natives = collect_natives(args.natives_dir, args.native_commit)
    models = collect_models(args.model, args.license, args.notice)
    credentials = None if args.dry_run else load_r2_credentials(SECRETS_PATH)
    manifest, files = build_manifest(natives, args.native_commit, models)
    if credentials is not None:
        upload_all(make_s3_client(credentials), files)
        verify_public(files)
    write_manifest(args.manifest, manifest)


def main(argv: list[str] | None = None) -> int:
    """Runs the tool.

    Args:
        argv: Arguments without the program name, or None for sys.argv.

    Returns:
        0 on success, 1 when an upload or the public check failed, 2 on bad input or missing credentials.
    """
    args = parse_args(argv)
    try:
        run(args)
    except PublishError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return exc.exit_code
    return 0


if __name__ == "__main__":
    sys.exit(main())
