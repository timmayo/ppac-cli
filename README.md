# ppac-cli

Command-line tool (`ppac.exe`) for Power Platform admin center tasks. Windows only. Commercial cloud first.

## Build and install locally

    dotnet pack src/Ppac.Cli -c Release
    dotnet tool install --global --add-source src/Ppac.Cli/nupkg Ppac.Cli

Update after changes:

    dotnet tool update --global --add-source src/Ppac.Cli/nupkg Ppac.Cli

Uninstall:

    dotnet tool uninstall --global Ppac.Cli