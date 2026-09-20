Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
directory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
launcher = fileSystem.BuildPath(directory, "skill-atlas-desktop.cmd")
shell.Run Chr(34) & launcher & Chr(34), 0, False
