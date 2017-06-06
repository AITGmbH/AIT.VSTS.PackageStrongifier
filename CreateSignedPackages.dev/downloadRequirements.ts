
"use strict";

import * as tl from "vsts-task-lib/task";
import * as fs from "fs";
import * as http from "http";
import * as os from "os";
import * as path from "path";
import * as AdmZip from "adm-zip";
import { IExecOptions } from "vsts-task-lib/toolrunner";

export async function restore() {
    let paket = path.join(__dirname, "bin", "paket.exe");
    
    var tpaket = tl.tool(paket);
    tpaket.arg ("restore");

    var modal: IExecOptions = {
        failOnStdErr: false,
        /** optional.  defaults to failing on non zero.  ignore will not fail leaving it up to the caller */
        ignoreReturnCode: true,
        /** optional working directory.  defaults to current */
        cwd: __dirname,
        /** optional envvar dictionary.  defaults to current process's env */
        env: null,
        /** optional.  defaults to fales */
        silent: false,
        outStream: null,
        errStream: null,
        /** optional.  foo.whether to skip quoting/escaping arguments if needed.  defaults to false. */
        windowsVerbatimArguments: false
    };
    var exitCode = await tpaket.exec(modal);
    if (exitCode != 0) {
      if (process.env.DEBUG == "true") {
        // Because I expect developer to look at the output AND
        // That they usually have everything restored most of the time
        // This makes development possible even when some packages fail with
        // The specified path, file name, or both are too long. The fully qualified file name must be less than 260 characters, and the directory name must be less than 248 characters.
        tl.warning("Ignoring paket restore failure because of DEBUG");
      } else {
        throw `paket restore failed with exit code ${exitCode}`
      }
    }
}


function downloadFile(url, dest) : Promise<void> {
  var file = fs.createWriteStream(dest);
  return new Promise<void>((resolve, reject) => {
    var responseSent = false; // flag to make sure that response is sent only once.
    http.get(url, response => {
      response.pipe(file);
      file.on('finish', () =>{
        file.close();
        //() => {
          if(responseSent)  return;
          responseSent = true;
          resolve();
        //}
      });
    }).on('error', err => {
        if(responseSent)  return;
        responseSent = true;
        reject(err);
    });
  });
};

async function installFake(installDir) {
  var tmpDir = os.tmpdir();
  var fakeZip = path.join(tmpDir, "fake.zip");
  await downloadFile("https://github.com/fsharp/FAKE/releases/download/5.0.0-alpha009/fake-dotnetcore-win7-x64.zip", fakeZip);
  var zip = new AdmZip(fakeZip);
  zip.extractAllTo(installDir, true);
  fs.unlink(fakeZip)
  fs.unlink(tmpDir)
}
