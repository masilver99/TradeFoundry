# TradeFoundry

- This is a .NET 10 local-first trading journal. Normal app data is .tradefoundry-data\journal.db; the normal local port is 5080 and the optional loopback MCP endpoint is 5081.
- For disposable validation, do not use the normal database or default bin/obj. Use a unique ignored .codex-build\<run> output and set Storage__DataDirectory to a unique writable data directory. Disable Benchmark__AutomaticRefresh and Mcp__Enabled unless the test covers them.
- Build and test from the exact isolated output:
  dotnet build TradeFoundry.csproj --no-restore --nologo -p:UseAppHost=false -p:OutDir=.codex-build\<run>\bin\
  dotnet test tests\TradeFoundry.Tests\TradeFoundry.Tests.csproj --no-restore --nologo --maxcpucount:1 -p:UseAppHost=false -p:OutDir=.codex-build\<run>\test\
- Start smoke hosts from that output on a unique port; record and stop only the disposable host before reusing output. Rebuild the configuration actually served before browser checks.
- Imported evidence and derived trades are immutable; user review changes belong in the revisioned review_key annotation layer.
