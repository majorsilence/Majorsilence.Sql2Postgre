#!/usr/bin/env bash
# Downloads the public sample databases the corpus tests run against into tests/corpus/
# (git-ignored; never commit them). All come from Microsoft's sql-server-samples repository.
# Each file is checked against the SHA-256 recorded below; a mismatch deletes it and fails.
#
#   bash scripts/download-corpus.sh            download what is missing, verify everything
#   bash scripts/download-corpus.sh --hashes   print the SHA-256 of what is present (to update the table)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$root/tests/corpus"
mkdir -p "$dest"

samples="https://github.com/microsoft/sql-server-samples"
raw="https://raw.githubusercontent.com/microsoft/sql-server-samples/master"

# name | url | sha256
files=(
  "AdventureWorks2022.bak|$samples/releases/download/adventureworks/AdventureWorks2022.bak|d17567adb1521f972e1dc183a7216cea869c4580b5d75632425bcadbaf82ce5e"
  "AdventureWorksDW2022.bak|$samples/releases/download/adventureworks/AdventureWorksDW2022.bak|ac4a39502645c31f114331be28ce671ac5f70b0645f2aa59d8dccfbaae081c05"
  "WideWorldImporters-Full.bak|$samples/releases/download/wide-world-importers-v1.0/WideWorldImporters-Full.bak|e842bad6ce02f74f166947e559dab1b476edd7eaae3da2ab9e3f522f1dd87124"
  "WideWorldImportersDW-Full.bak|$samples/releases/download/wide-world-importers-v1.0/WideWorldImportersDW-Full.bak|e2f4a37c2db3aa494ca8f7bee2f52372b3d2cceac2ec7e3aa3b140bd1e77bee5"
  "instnwnd.sql|$raw/samples/databases/northwind-pubs/instnwnd.sql|3cc62b3fca6d244a47dbde698b809331e4f85988a0685b2b370717d431e94871"
  "instpubs.sql|$raw/samples/databases/northwind-pubs/instpubs.sql|c66479d429f482ef788290dd94bb315f2277327765f480b2b21be6f359eb4bad"
)

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

if [[ "${1:-}" == "--hashes" ]]; then
  for entry in "${files[@]}"; do
    IFS='|' read -r name _ _ <<<"$entry"
    [[ -f "$dest/$name" ]] && echo "$name $(sha256 "$dest/$name")"
  done
  exit 0
fi

for entry in "${files[@]}"; do
  IFS='|' read -r name url expected <<<"$entry"
  target="$dest/$name"
  if [[ ! -f "$target" ]]; then
    echo "Downloading $name"
    curl -fL --retry 3 -o "$target.part" "$url"
    mv "$target.part" "$target"
  fi
  actual="$(sha256 "$target")"
  if [[ "$actual" != "$expected" ]]; then
    echo "error: $name has SHA-256 $actual, expected $expected; deleted." >&2
    rm -f "$target"
    exit 1
  fi
  echo "ok $name"
done
