if (args is ["prepare-browser", .. var install])
    return Microsoft.Playwright.Program.Main(["install", "chromium", .. install]);
return await EyeAuras.Automation.Cli.AutomationCli.RunAsync(args);
