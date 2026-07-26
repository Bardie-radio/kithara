# gRPC Blob Storage (v0.1 draft)

Thin **put/get** API hosted on **Kithara**. Source modules (Magpie, Catbird, …) **dial Kithara** over mTLS — drivers stay inside Kithara only ([storage](../domains/storage.md), [ADR 010](../adrs/010-blob-storage-backends.md)).

**Status:** v0.1 draft — RPC set and dial rules are frozen; packages published on nuget.org as `Bardie.Logos.Contracts`. Checked-in proto: [`blob_storage.proto`](https://github.com/Bardie-radio/logos/blob/main/src/Bardie.Logos.Contracts/Protos/blob_storage.proto) in `Bardie.Logos.Contracts` (wire package `bardie.storage.v1`).

```protobuf
syntax = "proto3";

package bardie.storage.v1;

option csharp_namespace = "Bardie.Storage.V1";

service BlobStorage {
  rpc Put(stream PutBlobRequest) returns (PutBlobResponse);
  rpc Get(GetBlobRequest) returns (stream GetBlobResponse);
  rpc Exists(ExistsBlobRequest) returns (ExistsBlobResponse);
  rpc Delete(DeleteBlobRequest) returns (DeleteBlobResponse);
}
```

## Key layout

Under the storage driver root (`BARDIE_STORAGE_PATH` for local):

| Pattern | Example |
|---------|---------|
| `tunes/<source_slug>/…` | `tunes/magpie/<object-id>` |

- Keys are **opaque** to callers; only the active driver maps key → filesystem path or object name.
- Keys must stay under `tunes/<source_slug>/` for the calling module — escape / traversal is rejected.
- **Put may omit `key`** — Kithara assigns a key under `tunes/<source_slug>/…` using the mTLS module identity (and/or request metadata). Response returns the assigned key.

## Streaming shape

```protobuf
message PutBlobHeader {
  string key = 1;           // empty → Kithara assigns
  string content_type = 2;
}

// First message MUST be header; subsequent messages are data chunks.
message PutBlobRequest {
  oneof payload {
    PutBlobHeader header = 1;
    bytes chunk = 2;
  }
}

message PutBlobResponse {
  string key = 1;
  int64 size_bytes = 2;
}

message GetBlobRequest {
  string key = 1;
}

message GetBlobResponse {
  string content_type = 1;  // typically on first frame
  bytes chunk = 2;
}
```

`Exists` / `Delete` are unary by key.

## Dial rules

- Service listens on Kithara gRPC (`:5000`); modules use ModuleChannel mTLS after Registry join.
- No parallel `BARDIE_STORAGE_*` on modules — config is Kithara-only.
- Session FIFOs are **not** blob storage ([ADR 004](../adrs/004-source-instance-socket-audio-plane.md)).

**Related:** [domains/storage.md](../domains/storage.md) · [grpc-library](grpc-library.md) · [grpc-source-module](grpc-source-module.md) · [ADR 010](../adrs/010-blob-storage-backends.md) · [Bardie.Logos.Contracts](https://github.com/Bardie-radio/logos/tree/main/src/Bardie.Logos.Contracts)

**Read next:** [grpc-library.md](grpc-library.md)
