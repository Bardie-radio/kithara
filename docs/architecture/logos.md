# Logos libraries

**Logos** is the portable module-mesh protocol extracted from Kithara so module authors (Bes, Magpie, Plume, …) can build without a Kithara checkout. Packages are on [nuget.org](https://www.nuget.org).

| Repo | Packages | Namespaces |
|------|----------|------------|
| [`Bardie-radio/logos`](https://github.com/Bardie-radio/logos) | `Bardie.Logos.Contracts`, `Bardie.Logos.Channel`, `Bardie.Logos.Hosting` | `Bardie.Logos.*` |
| [`Bardie-radio/kithara-logos-auth`](https://github.com/Bardie-radio/kithara-logos-auth) | `Bardie.Module.Auth` | `Bardie.Module.Auth` (unchanged) |
| [`Bardie-radio/kithara-logos-source`](https://github.com/Bardie-radio/kithara-logos-source) | `Bardie.Module.Source`, `Bardie.Module.Source.Debug` | `Bardie.Module.Source` (unchanged) |

Consumers take **`PackageReference`** (pin versions in `Directory.Packages.props`). Kithara keeps host-only harnesses in-tree (`libs/Bardie.Harness.Auth`, `libs/Bardie.Harness.Source`) and references the same nuget.org packages.

See [glossary](glossary.md), [module-channel](operations/module-channel.md), [02-internal-structure](overview/02-internal-structure.md).
