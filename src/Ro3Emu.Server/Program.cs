using Ro3Emu.Server;

// Offline restore of a backup (the server must be stopped): Ro3Emu.Server --restore-backup <file> [config args]
if (args is ["--restore-backup", var backup, .. var rest])
    return Ro3Emu.Server.Accounts.DatabaseRestore.RunCli(backup, ServerHost.ResolveOptions(rest), Console.Out);

ServerHost.Create(args).Run();
return 0;
