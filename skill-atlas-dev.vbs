Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
directory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
launcher = fileSystem.BuildPath(directory, "skill-atlas-dev.cmd")
shell.Run Chr(34) & launcher & Chr(34) & " --hidden", 0, False
