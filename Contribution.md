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
./packages/FAKE/tools/FAKE.exe processPackages.fsx

# Test the nodejs integration
npm run tsc
node startProcess.js

```

And test the logic.

# Debugging the F# script

1. Clone FAKE (https://github.com/fsharp/FAKE)
2. Setup Environment variables (as above) and start Visual Studio with those 
   easiest is to setup environment variables in git bash (as above) and run
   `"/c/Program Files (x86)/Microsoft Visual Studio/2017/Enterprise/Common7/IDE/devenv.exe"`
3. Setup arguments and working dir in the FAKE project
4. Start Debugging (make sure the solution is in DEBUG configuration).