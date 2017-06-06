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