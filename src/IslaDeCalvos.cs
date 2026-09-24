namespace Oxide.Plugins
{
    [Info("Isla de Calvos", "Igor Monasterio", "0.1.0")]
    [Description("Main plugin for the Isla de Calvos Rust server.")]
    public class IslaDeCalvos : RustPlugin
    {
        private void Init()
        {
            Puts("Isla de Calvos loaded.");
        }
    }
}
