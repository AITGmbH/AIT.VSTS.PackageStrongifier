# How to build

```shell

# First time (https://chocolatey.org/)
choco install nodejs.install # if not already installed
choco install fake -pre

# create new version
fake run build.fsx

```

# How to run standalone (development)

First restore all depencies `fake run build.fsx`
Then enter the task directory `cd CreateSignedPackages.dev`

And restore task dependencies `./bin/paket.exe restore`

> Note: this is only required when testing the fake script, node will call paket restore automatically

Now you can run (in git bash):

```bash

# Set the parameters, DEBUG indicates that we will read them from environment.
export DEBUG=true
export PackageList="
source https://nuget.org/api/v2

nuget gong-wpf-dragdrop
nuget Prism.DryIoc
"
export SnkFile="$(realpath "../key.snk")"
export TargetFeed=""
export SignedPackagePostfix=".Signed"

# Test the fake script
./bin/Fake/FAKE.exe processPackages.fsx

# Test the nodejs integration
npm run tsc
node startProcess.js

```

And test the logic.

# 20MB Limitation

Extract the vsix (is a regular zip file) and compress it again with 