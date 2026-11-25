# qb-import-mgr: Build and Publish

qb-import-mgr turns bank and Amex Web Connect downloads (.QBO) into IIF files for QuickBooks Desktop 2020. This sheet covers building, testing and publishing it.

## Locations

| Item | Path |
|---|---|
| Source code and git repo | C:\git\qb-import-mgr |
| Published app | C:\accounting\qb-import-mgr.exe |
| Database | C:\accounting\qb-import-mgr.db |
| Bank downloads (inbox) | C:\accounting\qbo |
| Imported originals | C:\accounting\qbo\archive |
| IIF output | C:\accounting\iif |
| Database backups | C:\accounting\backup |

## Prerequisites

1. .NET 10 SDK. Check with:

   **C:\> dotnet --list-sdks**

   A line starting with 10. must appear. If not, install it:

   **C:\> winget install Microsoft.DotNet.SDK.10**

2. Git for Windows. Check with:

   **C:\> git --version**

3. Claude Code (optional, for making changes). Check with:

   **C:\> claude --version**

   The folder %USERPROFILE%\.local\bin must be on your user PATH.

## Visual Studio note

Visual Studio 2022 (version 17.x) cannot build .NET 10 projects. It reports error NETSDK1045: "The current Visual Studio version does not support targeting .NET 10.0." The project itself is fine.

- To build, test and publish, use the command line (below) or Claude Code.
- To debug with breakpoints or use the WPF designer, install Visual Studio 2026 Community (version 18). It is free and installs alongside VS 2022.
- Do not drop the project back to .NET 9. .NET 9 support ended in November 2026.

## Build and test

Open a Command Prompt in the source folder:

**C:\> cd C:\git\qb-import-mgr**

Build:

**C:\git\qb-import-mgr> dotnet build**

Run the tests. All must pass, with 0 warnings:

**C:\git\qb-import-mgr> dotnet test**

## Publish a new exe

1. Close qb-import-mgr if it is running. Windows locks a running exe, and the publish will fail.

2. Publish to C:\accounting:

   **C:\git\qb-import-mgr> dotnet publish src\QbImportMgr.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o C:\accounting**

3. Check that the Date modified on C:\accounting\qb-import-mgr.exe has changed.

4. Start the app. The title bar should show C:\accounting, with no yellow DEV banner.

Publishing replaces only the exe. The database, the folders and your rules are not touched. If the database schema changed, the app backs up the database to C:\accounting\backup before upgrading it.

## Publish through Claude Code

In the Claude Code terminal, at C:\git\qb-import-mgr:

**C:\git\qb-import-mgr> claude**

Then ask: "Build, test, commit and republish to C:\accounting."

## Test without touching real data

Debug builds use C:\accounting-dev automatically. To point the published exe at another folder:

**C:\> C:\accounting\qb-import-mgr.exe --data-dir D:\qbim-test**

## Troubleshooting

| Symptom | Fix |
|---|---|
| 'dotnet' is not recognized | Open a new terminal tab. If that doesn't help, add C:\Program Files\dotnet\ to the System Path and restart Visual Studio. |
| 'claude' is not recognized | Add %USERPROFILE%\.local\bin to the User Path and restart Visual Studio. |
| NETSDK1045 in Visual Studio 2022 | Expected. Build from the command line, or use VS 2026. |
| Publish fails: file in use | Close qb-import-mgr, then publish again. |
| A terminal tab has lost its PATH | Close the tab and open a new one. |
| QuickBooks rejects an IIF with errors 10143 / 11114 | The vendor name is on the SPL line. Delete the name from the SPL lines, then re-import only the error file QuickBooks wrote. |