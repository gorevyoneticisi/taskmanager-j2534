[.NET Core CLR (coreclr)](https://github.com/3F/coreclr)
=========================

[![Build status](https://ci.appveyor.com/api/projects/status/asb0nbj8tly2rp7p/branch/master?svg=true)](https://ci.appveyor.com/project/3Fs/coreclr-62ql7/branch/master)
[![License](https://img.shields.io/badge/License-MIT-74A5C2.svg)](https://github.com/3F/coreclr/blob/master/LICENSE.TXT)

.NET Core complete runtime implementation. Also includes IL Assembler, IL Disassembler, RyuJIT, the .NET GC, native interop and many other components.

Some of these components have been **modified** and also known as *3F's IL Assembler on coreclr* ([github/3F](https://github.com/3F))
specialy for [https://github.com/3F/DllExport](https://github.com/3F/DllExport) and for other related purposes.

Licensed under the [MIT License (MIT)](https://github.com/3F/coreclr/blob/master/LICENSE.TXT)

```r
Copyright (c) .NET Foundation and Contributors
Copyright (c) 2016-2025  Denis Kuzmin <x-3F@outlook.com> github/3F
```

## ILAsm on coreclr

3F's IL Assembler (ILAsm) + IL Disassembler (ILDasm) https://github.com/3F/coreclr

[`gnt`](https://3F.github.io/GetNuTool/releases/latest/gnt/)`ILAsm` [[?](https://github.com/3F/GetNuTool)]
[![NuGet package](https://img.shields.io/nuget/v/ILAsm.svg)](https://www.nuget.org/packages/ILAsm/)
[![release](https://img.shields.io/github/release/3F/coreclr.svg)](https://github.com/3F/coreclr/releases/latest)

### /REBASE

[Rebase](https://github.com/3F/DllExport/pull/123) system object in order `netstandard` \> `System.Runtime` \> `mscorlib` is available starting from 4.700.2+

#### .typeref custom definitions

Starting from 9.3.0+ you can change the binding of any type for specific assemblies using the following `.typeref` directive:

grammar:

```yacc
assemblyDecl : '.hash' 'algorithm' int32 
            | secDecl
            | asmOrRefDecl
            | '.typeref' dottedName 'at' dottedName 
            | '.typeref' dottedName 'any' 'at' dottedName 
            | '.typeref' dottedName 'constraint' 'deny' 
            | '.typeref' dottedName 'constraint' 'any' 'deny' 
            | '.typeref' dottedName 'assert' 
            | '.typeref' dottedName 'any' 'assert' 
            ;
```

The format for changing the link:

```csharp
.typeref 'Type' [any] at 'ResolutionScope'
```

Assertion of type declaration by current module (first at *Module* tables):

```csharp
.typeref 'Type' [any] assert
//alias: .typeref 'Type' [any] at ''
```

The format for rejecting defined records (predefined by the user and the .assembler declaration):

```csharp
.typeref 'Type' constraint [any] deny
```

Note keyword `any` points to multiple definitions starting from the specified *Name*

For example, predefined assembler's `.typeref` directives (when */REB* is activated) will mean something like: 

```csharp
// 9.3.0+
.assembly 'ClassLibrary1'
{
    .typeref 'System.' any at 'mscorlib'
    .typeref 'System' at 'mscorlib'
    .typeref 'System.Span`' any assert
    .typeref 'System.ReadOnlySpan`' any assert
    .typeref 'System.Memory`' any assert
    .typeref 'System.ReadOnlyMemory`' any assert
    .typeref 'System.MemoryExtensions' assert
    
    .custom instance void ...
    .custom instance void ...
    .hash algorithm 0x00008004
    .ver 1:0:0:0
}
```

Multiple definitions are competitive or interchangeable. Priority is given to the last from top to bottom. For example,

```csharp
.typeref 'System.' any at 'System.Runtime'
.typeref 'System.Math' constraint deny
.typeref 'System.IO.' constraint any deny
.typeref 'System.' any at 'mscorlib'
.typeref 'System.' any assert
```

are equal to

```csharp
.typeref 'System.' any assert
```

etc.

### /CVRES

.res / .obj

In order to provide a compatible resource converter to obj COFF-format when assembling, use [/CVRES](https://github.com/3F/coreclr/issues/2) (/CVR) key:

```
ilasm ... /CVR=cvtres.exe
```

#### Automatic search for resource converter

Automatic search is available starting from 9.3.0+ by using [hMSBuild](https://github.com/3F/hMSBuild) and other predefined falbacks to resolve *.res* / *.obj* processing automatically.

## NuGet Package Preferences

* `$(ILAsm_RootPkg)` - path to ILAsm package folder.
* `$(ILAsm_PathToBin)` - path to `\bin` folder., e.g. *$(ILAsm_PathToBin)Win.x64\ilasm.exe*
* `$(ILAsm_W64Bin)` and `$(ILAsm_W86Bin)` - e.g. *$(ILAsm_W64Bin)ildasm.exe*

Symbols (**PDB**) are available through GitHub Releases:
https://github.com/3F/coreclr/releases
