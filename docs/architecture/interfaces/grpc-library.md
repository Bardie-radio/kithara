# gRPC Library (v0.1 draft)

Modules **dial Kithara** to upsert shared-library **Tunes**. Kithara owns EF persistence ([library-and-tunes](../domains/library-and-tunes.md), [ADR 006](../adrs/006-stream-source-tune-data-model.md)).

**Status:** v0.1 draft — RPC set and dial rules are frozen; packages published on nuget.org as `Bardie.Logos.Contracts`. Checked-in proto: [`library.proto`](https://github.com/Bardie-radio/logos/blob/main/src/Bardie.Logos.Contracts/Protos/library.proto) in `Bardie.Logos.Contracts` (wire package `bardie.library.v1`).

```protobuf
syntax = "proto3";

package bardie.library.v1;

option csharp_namespace = "Bardie.Library.V1";

service Library {
  rpc EnsureTune(EnsureTuneRequest) returns (EnsureTuneResponse);
}
```

## `EnsureTune`

Upsert by **`module_slug` + `external_id`**. Typical Magpie path after cache-miss download: `BlobStorage.Put` → `EnsureTune` with the returned storage key + metadata.

```protobuf
message EnsureTuneRequest {
  string module_slug = 1;
  string external_id = 2;
  string title = 3;
  string artist = 4;
  double duration_seconds = 5;  // 0 / unset = unknown
  string artwork_url = 6;
  string storage_key = 7;       // opaque blob key when bytes exist
  string content_type = 8;
  int64 size_bytes = 9;
}

message EnsureTuneResponse {
  string tune_id = 1;  // GUID of the Tune row
  bool created = 2;
}
```

Invariants (frozen for v0.1):

1. **Kithara owns** the Tune row and any blob GC policy; modules do not open the library DB.
2. **Sparse Tunes are valid** — storage fields may be empty (e.g. Starling URI-only).
3. Dial over ModuleChannel mTLS on Kithara `:5000`, same surface as [BlobStorage](grpc-blob-storage.md).

**Related:** [domains/library-and-tunes.md](../domains/library-and-tunes.md) · [grpc-blob-storage](grpc-blob-storage.md) · [grpc-source-module](grpc-source-module.md) · [ADR 006](../adrs/006-stream-source-tune-data-model.md) · [Bardie.Logos.Contracts](https://github.com/Bardie-radio/logos/tree/main/src/Bardie.Logos.Contracts)

**Read next:** [grpc-auth-adapter.md](grpc-auth-adapter.md)
