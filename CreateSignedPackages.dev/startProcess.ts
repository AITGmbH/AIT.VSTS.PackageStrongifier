
"use strict";

import * as tl from "vsts-task-lib/task";
import * as fs from "fs";
import * as os from "os";
import * as tmp from "tmp";
import * as path from "path";
import * as dl from "./downloadRequirements";
import { IExecOptions } from "vsts-task-lib/toolrunner";

function exitWithError(message, exitCode) {
  tl.error(message);
  tl.setResult(tl.TaskResult.Failed, message);
  process.exit(exitCode);
}

// See https://stackoverflow.com/a/32197381/1269722
var deleteFolderRecursive = function(pathToDelete) {
  if( fs.existsSync(pathToDelete) ) {
    fs.readdirSync(pathToDelete).forEach(function(file,index){
      var curPath = path.join(pathToDelete, file);
      if(fs.lstatSync(curPath).isDirectory()) { // recurse
        deleteFolderRecursive(curPath);
      } else { // delete file
        fs.unlinkSync(curPath);
      }
    });
    fs.rmdirSync(pathToDelete);
  }
};

async function doMain() {
  try {
    await dl.restore();
    tmp.setGracefulCleanup();
    let fakeExe = path.join(__dirname, "packages", "Fake", "tools", "FAKE.exe");
    let scriptFile = path.join(__dirname, "processPackages.fsx");
    var tmpDir = tmp.dirSync();
    var cwd = process.cwd();

    var isDebug = process.env.DEBUG == "true";
    var requireWhenRelease = true;
    if (isDebug) {
      requireWhenRelease = false;
    }

    // read inputs
    var packageList = tl.getInput("packageList", requireWhenRelease);
    if (packageList === undefined || packageList === null) {
      if (isDebug) {
        packageList = process.env.PackageList
      } else {
        throw "Expected packageList data"
      }
    }

    var snkFile = tl.getPathInput("snkFile", requireWhenRelease);
    if (snkFile === undefined || snkFile === null) {
      if (isDebug) {
        snkFile = process.env.SnkFile
      } else {
        throw "Expected snkFile data"
      }
    }
    var snkPassword = tl.getInput("snkPassword");
    if (snkPassword === undefined || snkPassword === null) {
      snkPassword = "";
    }
    var targetFeed = tl.getInput("targetFeed");
    if (targetFeed === undefined || targetFeed === null) {
      if (isDebug) {
        targetFeed = process.env.TargetFeed
      } else {
        throw "Expected targetFeed data"
      }
    }

    var signedPackagePostfix = tl.getInput("signedPackagePostfix");
    if (signedPackagePostfix === undefined || signedPackagePostfix === null) {
      if (isDebug) {
        signedPackagePostfix = process.env.SignedPackagePostfix
      } else {
        signedPackagePostfix = "";
      }
    }

    var outDir = tl.getPathInput("outputDirectory");
    if (outDir === undefined || outDir === null) {
      outDir = path.join(cwd, "SignedPackages");
    }

    var fake = tl.tool(fakeExe);
    fake.arg(scriptFile)

    var modal: IExecOptions = {
        failOnStdErr: false,
        /** optional.  defaults to failing on non zero.  ignore will not fail leaving it up to the caller */
        ignoreReturnCode: false,
        /** optional working directory.  defaults to current */
        cwd: tmpDir.name,
        /** optional envvar dictionary.  defaults to current process's env */
        env: {
            "PackageList": packageList,
            "OutputDirectory": outDir,
            "TargetFeed": targetFeed,
            "SnkFile": snkFile,
            "SnkPassword": snkPassword,
            "SignedPackagePostfix": signedPackagePostfix
        },
        /** optional.  defaults to fales */
        silent: false,
        outStream: null,
        errStream: null,
        /** optional.  foo.whether to skip quoting/escaping arguments if needed.  defaults to false. */
        windowsVerbatimArguments: false
    };

    tl.debug(`Running FAKE in '${tmpDir.name}'`);
    var exitCode = await fake.exec(modal);
    tl.debug("Doing some cleanup");
    deleteFolderRecursive(tmpDir.name);
    if (exitCode != 0) {
      throw `FAKE process failed with exit code '${exitCode}'`;
    }
  } catch (e) {
    exitWithError(e.message, 1);
  }
}

doMain()