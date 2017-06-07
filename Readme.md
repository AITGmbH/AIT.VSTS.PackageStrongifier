# AIT.VSTS.PackageSigning

The Package Signing build task allows you to download NuGet packages, sign them and create signed packages in your own NuGet feed.
This simplifies the usage of unsigned NuGet packages as you just use the signed version of your own feed just like any other package.

Other solutions like https://github.com/brutaldev/StrongNameSigner, https://www.nuget.org/packages/Nivot.StrongNaming/ or similar approaches change your build pipeline
and hook into msbuild and sign references before doing the actual compilation.
This complicates the build process and makes failures a lot more difficult to analyse and debug. 

## Build

See Contribution.md

## How to 

1. Build (see Contribution.md)
2. Install extension (upload on http://tfs.myserver:8080/tfs/_gallery/manage)
   See https://stackoverflow.com/questions/40810914/how-do-you-install-extension-vsix-files-to-tfs-2015-update-3
3. TODO


## Technical details

# We use the follwoing projects

- Paket - https://github.com/fsprojects/Paket
  - To resolve the dependency tree
  - dynamically load dependencies in the build task (when bundling everything we go above the 20MB limit for vsts extensions)
- Fake - https://github.com/fsharp/FAKE/
  - For running the `processPackages.fsx` F# script without dependencies and platform independent
  - For building the final package "Package" (the vsix), see `build.fsx`
- Mono.Cecil - https://github.com/jbevain/cecil
  - for rewriting/signing the assemblies
- NuGet.CommandLine
  - for creating the signed packages (well paket could do that, but this way we can just use the existing NuSpec)
- MsBuild components - https://www.nuget.org/packages/Microsoft.Build.Utilities.Core/
  - Finding the correct references for Mono.Cecil


# References

- reference for build tasks:
  - https://www.visualstudio.com/en-us/docs/integrate/extensions/develop/add-build-task
  - https://www.visualstudio.com/en-us/docs/integrate/extensions/develop/build-task-schema
  - https://github.com/Microsoft/vsts-tasks

## Recommendations

- Use the postfix and add some kind of versioning for the generator "Signed-v1".
- Update this postfix when the version of this task changes or you need a "old" package build with the new version.

This has the following disadvantages:
   - Visibility of new versions
   - Some overhead when changing the generator version

But all in all it's a more robust way of doing things. 

## Upgrade generator "Signed-v1" -> "Signed-v2"

- Search and replace the ".Signed-v1" postfix with ".Signed-v2" in all the package.config files.
- If you run into problems, try:
   - Delete all nuget references from the project file
   - Update-Package -reinstall -Project YourProjectName