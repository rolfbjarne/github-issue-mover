all: bin/Debug/github-issue-mover.exe

bin/Debug/github-issue-mover.exe: $(wildcard *.cs *.csproj packages.config)
	nuget restore
	msbuild $(wildcard *.csproj)