"""Tests for tools/publish_stt_downloads.py.

The tool is a script, not an installed module, so it is loaded by path. Every file it reads or writes
lives under `tmp_path`; the S3 client is a stub, since the network is the boundary.
"""

from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
from pathlib import Path
from typing import Any

import pytest
from botocore.exceptions import ClientError

TOOL_PATH = Path(__file__).resolve().parents[1] / "publish_stt_downloads.py"
COMMIT = "804a1d0c29121a01853916698d4a9b87851bed2e"
NESTED_ZIPS = ("win-x64-cpu.zip", "linux-x64-cuda.zip", "osx-arm64.zip")
PLAIN_ZIPS = ("win-x64-cuda.zip", "linux-x64-cpu.zip", "osx-x64.zip")
FP32_FILES = ("encoder.onnx", "encoder.weights", "decoder.onnx", "joiner.onnx", "tokens.txt", "bpe.vocab")
INT8_FILES = ("encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt", "bpe.vocab")
BAD_INPUT = 2


def _load_tool() -> Any:
    spec = importlib.util.spec_from_file_location("publish_stt_downloads", TOOL_PATH)
    assert spec is not None
    assert spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules["publish_stt_downloads"] = module
    spec.loader.exec_module(module)
    return module


tool = _load_tool()


def _write(path: Path, text: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")
    return path


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


@pytest.fixture
def inputs(tmp_path: Path) -> dict[str, Path]:
    natives = tmp_path / "natives"
    for name in NESTED_ZIPS:
        _write(natives / name / name, f"zip {name}")
    for name in PLAIN_ZIPS:
        _write(natives / name, f"plain zip {name}")
    for name in FP32_FILES:
        _write(tmp_path / "fp32" / name, f"fp32 {name}")
    for name in INT8_FILES:
        _write(tmp_path / "int8" / name, f"int8 {name}")
    return {
        "natives": natives,
        "fp32": tmp_path / "fp32",
        "int8": tmp_path / "int8",
        "license": _write(tmp_path / "LICENSE.txt", "licence text"),
        "notice": _write(tmp_path / "NOTICE.txt", "notice text"),
        "manifest": tmp_path / "out" / "stt-downloads.json",
    }


def _argv(inputs: dict[str, Path], *extra: str) -> list[str]:
    return [
        "--natives-dir",
        str(inputs["natives"]),
        "--native-commit",
        COMMIT,
        "--model",
        f"int8={inputs['int8']}",
        "--model",
        f"fp32={inputs['fp32']}",
        "--license",
        str(inputs["license"]),
        "--notice",
        str(inputs["notice"]),
        "--manifest",
        str(inputs["manifest"]),
        *extra,
    ]


def test_dry_run_writes_manifest_in_documented_shape_and_order(inputs: dict[str, Path]) -> None:
    assert tool.main(_argv(inputs, "--dry-run")) == 0

    raw = inputs["manifest"].read_bytes()
    assert raw.endswith(b"}\n")
    assert b"\r" not in raw
    assert raw.startswith(b'{\n  "baseUrl"')
    manifest = json.loads(raw)
    assert list(manifest) == ["baseUrl", "natives", "models"]
    assert manifest["baseUrl"] == "https://dl.leftos.dev/"
    assert list(manifest["natives"]) == ["commit", "mirrors", "files"]
    assert manifest["natives"]["commit"] == COMMIT
    assert manifest["natives"]["mirrors"] == []

    natives = manifest["natives"]["files"]
    assert [(entry["rid"], entry["variant"]) for entry in natives] == [
        ("linux-x64", "cpu"),
        ("linux-x64", "cuda"),
        ("osx-arm64", "cpu"),
        ("osx-x64", "cpu"),
        ("win-x64", "cpu"),
        ("win-x64", "cuda"),
    ]
    win_cpu = natives[4]
    assert list(win_cpu) == ["rid", "variant", "key", "size", "sha256"]
    assert win_cpu["key"] == "sherpa-native/804a1d0c29/win-x64-cpu.zip"
    nested_zip = inputs["natives"] / "win-x64-cpu.zip" / "win-x64-cpu.zip"
    assert win_cpu["size"] == nested_zip.stat().st_size
    assert win_cpu["sha256"] == _sha256(nested_zip)
    plain_zip = inputs["natives"] / "linux-x64-cpu.zip"
    assert natives[0]["sha256"] == _sha256(plain_zip)

    models = manifest["models"]
    assert [model["id"] for model in models] == ["rasr-parakeet-v2-fp32", "rasr-parakeet-v2-int8"]
    assert list(models[0]) == ["id", "files"]
    fp32_names = [file["name"] for file in models[0]["files"]]
    int8_names = [file["name"] for file in models[1]["files"]]
    assert fp32_names == sorted([*FP32_FILES, "LICENSE", "NOTICE"])
    assert int8_names == sorted([*INT8_FILES, "LICENSE", "NOTICE"])
    encoder = next(file for file in models[0]["files"] if file["name"] == "encoder.weights")
    assert list(encoder) == ["name", "key", "size", "sha256"]
    assert encoder["key"] == "stt/rasr-parakeet-v2/fp32/encoder.weights"
    assert encoder["sha256"] == _sha256(inputs["fp32"] / "encoder.weights")
    licence = next(file for file in models[1]["files"] if file["name"] == "LICENSE")
    assert licence["key"] == "stt/rasr-parakeet-v2/int8/LICENSE"
    assert licence["sha256"] == _sha256(inputs["license"])


def test_mirror_values_land_in_natives_mirrors_in_order(inputs: dict[str, Path]) -> None:
    first = "https://github.com/leftos/sherpa-onnx/releases/download/yaat-natives-804a1d0c29/"
    second = "https://mirror.example/natives/"

    assert tool.main(_argv(inputs, "--dry-run", "--mirror", first, "--mirror", second)) == 0

    manifest = json.loads(inputs["manifest"].read_bytes())
    assert list(manifest["natives"]) == ["commit", "mirrors", "files"]
    assert manifest["natives"]["mirrors"] == [first, second]


def test_absent_mirror_writes_empty_mirrors(inputs: dict[str, Path]) -> None:
    assert tool.main(_argv(inputs, "--dry-run")) == 0

    manifest = json.loads(inputs["manifest"].read_bytes())
    assert manifest["natives"]["mirrors"] == []


def test_macos_zips_map_to_cpu_variant_and_keep_their_own_names(inputs: dict[str, Path]) -> None:
    natives = tool.collect_natives(inputs["natives"], COMMIT)

    macos = {native.rid: native for native in natives if native.rid.startswith("osx-")}
    assert sorted(macos) == ["osx-arm64", "osx-x64"]
    assert macos["osx-arm64"].variant == "cpu"
    assert macos["osx-x64"].variant == "cpu"
    assert macos["osx-arm64"].file.key == "sherpa-native/804a1d0c29/osx-arm64.zip"
    assert macos["osx-x64"].file.key == "sherpa-native/804a1d0c29/osx-x64.zip"


def test_missing_model_file_is_an_error_naming_it(inputs: dict[str, Path], capsys: pytest.CaptureFixture[str]) -> None:
    (inputs["int8"] / "joiner.int8.onnx").unlink()

    assert tool.main(_argv(inputs, "--dry-run")) == BAD_INPUT

    assert "joiner.int8.onnx" in capsys.readouterr().err
    assert not inputs["manifest"].exists()


def test_missing_secrets_key_exits_2_without_printing_values(
    inputs: dict[str, Path], tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    secrets = _write(tmp_path / "r2.env", "R2_ACCOUNT_ID=acct-value-1234\nR2_ACCESS_KEY_ID=access-value-5678\n")
    monkeypatch.setattr(tool, "SECRETS_PATH", secrets)

    assert tool.main(_argv(inputs)) == BAD_INPUT

    captured = capsys.readouterr()
    output = captured.out + captured.err
    assert "R2_SECRET_ACCESS_KEY" in captured.err
    assert str(secrets) in captured.err
    assert "acct-value-1234" not in output
    assert "access-value-5678" not in output
    assert not inputs["manifest"].exists()


class StubS3:
    """Answers head_object from a dict of existing objects and records upload_file calls."""

    def __init__(self, objects: dict[str, dict[str, Any]]) -> None:
        self.objects = objects
        self.uploads: list[tuple[str, str, dict[str, Any]]] = []

    def head_object(self, Bucket: str, Key: str) -> dict[str, Any]:
        assert Bucket == "yaat-downloads"
        if Key not in self.objects:
            raise ClientError({"Error": {"Code": "404", "Message": "Not Found"}}, "HeadObject")
        return self.objects[Key]

    def upload_file(self, Filename: str, Bucket: str, Key: str, ExtraArgs: dict[str, Any], Config: Any) -> None:
        assert Bucket == "yaat-downloads"
        assert Config is not None
        self.uploads.append((Filename, Key, ExtraArgs))


def _hashed(path: Path, key: str) -> Any:
    return tool.HashedFile(key, path, path.stat().st_size, _sha256(path))


def test_existing_object_with_matching_size_and_hash_is_skipped(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    same = _hashed(_write(tmp_path / "same.zip", "same bytes"), "sherpa-native/804a1d0c29/same.zip")
    stale = _hashed(_write(tmp_path / "tokens.txt", "new tokens"), "stt/rasr-parakeet-v2/fp32/tokens.txt")
    absent = _hashed(_write(tmp_path / "encoder.onnx", "encoder"), "stt/rasr-parakeet-v2/fp32/encoder.onnx")
    client = StubS3(
        {
            same.key: {"ContentLength": same.size, "Metadata": {"sha256": same.sha256}},
            stale.key: {"ContentLength": stale.size, "Metadata": {"sha256": "0" * 64}},
        }
    )

    tool.upload_all(client, [same, stale, absent])

    assert f"skip {same.key}" in capsys.readouterr().out
    assert client.uploads == [
        (str(stale.path), stale.key, {"ContentType": "text/plain; charset=utf-8", "Metadata": {"sha256": stale.sha256}}),
        (str(absent.path), absent.key, {"ContentType": "application/octet-stream", "Metadata": {"sha256": absent.sha256}}),
    ]
