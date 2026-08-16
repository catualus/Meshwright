namespace Meshwright.Cli
{
    /// <summary>
    /// The meshwright executable.
    ///
    /// Everything real lives in the Meshwright library so Compile Pal can link it directly: a self-contained
    /// application cannot reference a non-self-contained executable, and shipping a sibling meshwright.exe
    /// alongside a single-file publish would mean carrying its runtime files too. This project exists
    /// only to give the same code a command line.
    /// </summary>
    internal static class Entry
    {
        private static int Main(string[] args) => Meshwright.Program.Main(args);
    }
}
