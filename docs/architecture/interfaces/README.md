# Interfaces

External and inter-service contracts.

| Page | Contract |
|------|----------|
| [rest-api.md](rest-api.md) | End-user HTTP API (UI modules call this) |
| [grpc-module-registry.md](grpc-module-registry.md) | All modules dial Kithara to Register (**v0.1 draft**) |
| [grpc-source-module.md](grpc-source-module.md) | Source work RPCs (incl. pause) — **v0.1 draft** |
| [grpc-auth-adapter.md](grpc-auth-adapter.md) | Auth work RPCs (UpdateUserBinding; AUTH-INVITE on host) — **v0.1 draft** |
| [grpc-blob-storage.md](grpc-blob-storage.md) | Modules dial Kithara for library blob Put/Get — **v0.1 draft** |
| [grpc-library.md](grpc-library.md) | Modules dial Kithara `EnsureTune` — **v0.1 draft** |
| [uri-routing.md](uri-routing.md) | Public URL map |
| [http-stream-output.md](http-stream-output.md) | `/stream/{slug}` ICY |
| [streaming-stack.md](streaming-stack.md) | FFmpeg + Stream Server |
| [auth.md](auth.md) | Discovery + permissions + guest users |

**Read next:** [grpc-module-registry.md](grpc-module-registry.md)
