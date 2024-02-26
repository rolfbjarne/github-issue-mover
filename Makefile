BIN=bin/Release/publish/github-issue-mover
all: $(BIN)

$(BIN): $(wildcard *.cs *.csproj packages.config)
	dotnet publish $(wildcard *.csproj) /bl:msbuild.binlog /p:SelfContained=true
