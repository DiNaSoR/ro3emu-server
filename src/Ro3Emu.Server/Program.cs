using Ro3Emu.Server;

// Offline restore of a backup (the server must be stopped): Ro3Emu.Server --restore-backup <file> [config args]
if (args is ["--restore-backup", var backup, .. var rest])
    return Ro3Emu.Server.Accounts.DatabaseRestore.RunCli(backup, ServerHost.ResolveOptions(rest), Console.Out);

// Checks content/*.json against the game data and prints the problems: Ro3Emu.Server --check-content [config args]
if (args is ["--check-content", .. var checkArgs])
    return Ro3Emu.Server.Data.CustomContentCheck.RunCli(ServerHost.ResolveOptions(checkArgs), Console.Out);

ServerHost.Create(args).Run();
return 0;
