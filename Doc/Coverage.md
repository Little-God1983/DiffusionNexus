# Code Coverage

Solution-wide line/branch coverage is collected via Coverlet driven by
`coverage.runsettings` at the repository root. Coverlet is already a
`PackageReference` on `DiffusionNexus.Tests` and `DiffusionNexus.IntegrationTests`,
so no per-project setup is required.

## One-shot local run

```pwsh
dotnet test DiffusionNexus.sln `
    --settings coverage.runsettings `
    --collect:"XPlat Code Coverage" `
    --results-directory TestResults
```

The collector emits `TestResults/<run-guid>/coverage.cobertura.xml` per
test project.

## HTML report

Install the global report renderer once:

```pwsh
dotnet tool install -g dotnet-reportgenerator-globaltool
```

Render after each test run:

```pwsh
reportgenerator `
    -reports:TestResults/**/coverage.cobertura.xml `
    -targetdir:Doc/CoverageReport `
    -reporttypes:Html
```

Open `Doc/CoverageReport/index.html`.

## What's excluded

`coverage.runsettings` excludes:
- EF Core migration scaffolding (`*Designer.cs`, `*ModelSnapshot.cs`)
- Avalonia XAML codebehind (`*.g.cs`)
- Anything under `obj/`
- The two test assemblies themselves
- Members tagged `[Obsolete]`, `[GeneratedCode]`, `[CompilerGenerated]`

When adding a new generated project or vendored binding, extend the
`ExcludeByFile` / `Exclude` blocks in `coverage.runsettings` rather than
silencing per-file.

## CI integration (not yet wired)

Once a baseline is captured, wire the same `dotnet test` invocation into
the GitHub Actions workflow and add a coverage-gate check via
`reportgenerator -targetdir:... -reporttypes:Cobertura;TextSummary` plus
a step that fails when `Line` or `Branch` drops below the agreed
threshold. Start at the current measurement, then ratchet upward.
