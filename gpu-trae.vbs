Option Explicit

Dim shell, fileSystem, traeLauncher, remoteUri, command, exitCode

Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

traeLauncher = "D:\software\Trae CN\bin\trae.cmd"
remoteUri = "vscode-remote://ssh-remote+mygpu/workspace/projects"

If Not fileSystem.FileExists(traeLauncher) Then
    MsgBox "TraeCode was not found: " & traeLauncher, vbExclamation, "CLI List"
    WScript.Quit 1
End If

command = shell.ExpandEnvironmentStrings("%ComSpec%") & " /d /c call """ & traeLauncher & """ --new-window --folder-uri """ & remoteUri & """"
exitCode = shell.Run(command, 0, True)

If exitCode <> 0 Then
    MsgBox "Failed to open the TraeCode remote window. Exit code: " & exitCode, vbExclamation, "CLI List"
End If
