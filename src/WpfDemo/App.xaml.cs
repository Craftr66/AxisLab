using System.Windows;
namespace AxisLab.WpfDemo;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);var window=new MainWindow();MainWindow=window;
        if(e.Args.Length==3&&e.Args[0]=="--direct-test")window.Loaded+=async(_,_)=>{
            try{await window.RunDirectChecks(e.Args[1],e.Args[2]);}
            catch(Exception ex){System.IO.File.WriteAllText(e.Args[2],System.Text.Json.JsonSerializer.Serialize(new{success=false,error=ex.ToString()}));Environment.ExitCode=1;}
            finally{window.Close();}
        };
        window.Show();
    }
}
