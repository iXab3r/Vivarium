import java.io.File;
import java.util.*;
import java.lang.reflect.Proxy;
import jetbrains.buildServer.ExtensionHolder;
import jetbrains.buildServer.agent.AgentLifeCycleListener;
import jetbrains.buildServer.agent.BuildRunnerContext;
import jetbrains.buildServer.powershell.agent.service.PowerShellServiceWindows;
import jetbrains.buildServer.util.EventDispatcher;
import jetbrains.buildServer.powershell.agent.*;
import jetbrains.buildServer.powershell.agent.detect.PowerShellInfo;
import jetbrains.buildServer.powershell.common.*;

public class PowerShellProbe {
  public static void main(String[] args) throws Exception {
    ShellInfoHolder holder = new ShellInfoHolder();
    holder.addShellInfo("owned-desktop", new PowerShellInfo(PowerShellBitness.x64, new File("C:/Windows/System32/WindowsPowerShell/v1.0"), "5.1", PowerShellEdition.DESKTOP, "powershell.exe"));
    holder.addShellInfo("owned-core", new PowerShellInfo(PowerShellBitness.x64, new File("C:/Program Files/PowerShell/7"), "7.5", PowerShellEdition.CORE, "pwsh.exe"));
    ExtensionHolder extensions = (ExtensionHolder)Proxy.newProxyInstance(ExtensionHolder.class.getClassLoader(), new Class<?>[]{ExtensionHolder.class}, (p,m,a)->null);
    EventDispatcher<AgentLifeCycleListener> events = EventDispatcher.create(AgentLifeCycleListener.class);
    PowerShellInfo selected = new PowerShellInfoProvider(extensions, null, null, events, holder).selectTool(PowerShellBitness.x64, null, null);
    Map<String,String> properties = new HashMap<>();
    properties.put("jetbrains_powershell_script_mode", "FILE");
    properties.put("jetbrains_powershell_script_file", "scripts/personal-release/windows.ps1");
    properties.put("jetbrains_powershell_execution", "PS1");
    properties.put("jetbrains_powershell_noprofile", "true");
    properties.put("jetbrains_powershell_bitness", "x64");
    BuildRunnerContext context = (BuildRunnerContext)Proxy.newProxyInstance(BuildRunnerContext.class.getClassLoader(),new Class<?>[]{BuildRunnerContext.class},(p,m,a)->{
      if (m.getName().equals("getConfigParameters")) return Collections.emptyMap();
      throw new IllegalStateException("Unexpected reference context call: "+m.getName());
    });
    PowerShellServiceWindows service = new PowerShellServiceWindows(null,null,null,null,null);
    // Bind only the empty ordinary configuration needed by this pure reference policy method.
    java.lang.reflect.Field runnerField = jetbrains.buildServer.agent.runner.CommandLineBuildService.class.getDeclaredField("myRunner");
    runnerField.setAccessible(true); runnerField.set(service,context);
    java.lang.reflect.Method policyMethod = PowerShellServiceWindows.class.getDeclaredMethod("useExecutionPolicy",PowerShellInfo.class);
    policyMethod.setAccessible(true);
    boolean usePolicy = (Boolean)policyMethod.invoke(service,selected);
    List<String> command = new PowerShellCommandLineProvider().provideCommandLine(selected, properties, new File("scripts/personal-release/windows.ps1"), usePolicy, Collections.emptyMap());
    System.out.println("CONTROL_JSON {\"edition\":"+quote(selected.getEdition().toString())+",\"executable\":"+quote(selected.getExecutable())+",\"useExecutionPolicy\":"+usePolicy+",\"arguments\":["+String.join(",",command.stream().map(PowerShellProbe::quote).toArray(String[]::new))+"]}");
  }
  private static String quote(String value) {return "\""+value.replace("\\","\\\\").replace("\"","\\\"").replace("\n","\\n").replace("\r","\\r")+"\"";}
}
