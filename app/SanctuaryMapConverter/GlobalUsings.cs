// The PowerShell driver (src/Import-MapGen.ps1) strips per-file usings and
// prepends exactly these three before Add-Type. Global usings reproduce that
// contract, so src/*.cs compiles identically in both worlds.
global using System;
global using System.IO;
global using System.Collections.Generic;
