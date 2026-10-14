# PipelineBar

Je Azure DevOps-pipelines in de menubalk (macOS) of het systeemvak (Windows), met een
melding zodra een pipeline start, slaagt, mislukt of op goedkeuring wacht.

- Het icoon kleurt zodra iets aandacht vraagt en toont hoeveel pipelines mislukt of bezig zijn.
- Builds en klassieke releases, gegroepeerd per project; klik om te openen in Azure DevOps.
- Filter op projecten, pipelinenaam (regex), periode en enkel je eigen runs.
- Negeer een pipeline (doorgestreept oog) tot hij een nieuwe run of status krijgt.

| Map        | Platform                     | Technologie         |
|------------|------------------------------|---------------------|
| `macOS/`   | macOS 14 of nieuwer          | SwiftUI             |
| `Windows/` | Windows 10 en 11             | WPF, .NET 10        |

## macOS

Vereist Xcode of de Command Line Tools (`xcode-select --install`).

    ./macOS/build.sh --install

Bouwt `build/PipelineBar.app`, kopieert hem naar `/Applications` (of `~/Applications`
zonder beheerdersrechten) en start hem.

## Windows

Vereist de [.NET 10 SDK](https://dotnet.microsoft.com/download). Het resultaat is één
zelfstandige `PipelineBar.exe`.

    .\Windows\build.ps1 -Install    # op Windows: installeert in %LOCALAPPDATA%\Programs
    ./Windows/build.sh [arm64]      # vanaf macOS of Linux: schrijft build/windows-x64/

De exe is niet ondertekend; SmartScreen vraagt de eerste keer om bevestiging
(Meer info → Toch uitvoeren).

## Instellen

Open Instellingen via het icoon, vul je organisatie in en plak een Personal Access Token
met de scopes **Build (Read)** en **Release (Read)**. Het token staat in de sleutelhanger
(macOS) of Windows Referentiebeheer, nooit in een bestand.
