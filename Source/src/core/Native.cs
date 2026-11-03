using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;

namespace AppManager
{
    /// <summary>Interop con le API di Windows usate dal programma.</summary>
    internal static class Native
    {
        // ------------------------------------------------------------------ Cestino

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400, FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>
        /// Sposta un file o una cartella nel Cestino, senza finestre di conferma. Se il Cestino non può accettarlo
        /// (troppo grande, unità senza Cestino) Windows chiede prima di eliminarlo definitivamente, invece di farlo in silenzio.
        /// </summary>
        public static void SendToRecycleBin(string path)
        {
            if (path.Length >= 260) throw new RecycleBinException(L.T("percorso troppo lungo per il Cestino"));
            var op = new SHFILEOPSTRUCT();
            op.wFunc = FO_DELETE;
            op.pFrom = path + "\0\0";
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING);
            int rc = SHFileOperation(ref op);
            if (op.fAnyOperationsAborted) throw new RecycleBinException(L.T("spostamento nel Cestino annullato"));
            if (rc != 0) throw new RecycleBinException(string.Format("codice 0x{0:X}", rc));
            // con alcuni errori SHFileOperation risponde 0 senza aver fatto nulla
            if (File.Exists(path) || Directory.Exists(path)) throw new RecycleBinException(L.T("l'elemento è ancora presente"));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string newName, int flags);

        /// <summary>Programma l'eliminazione di un file al prossimo riavvio (serve l'amministratore).</summary>
        public static bool DeleteAtReboot(string path)
        {
            return MoveFileEx(path, null, 0x4 /* MOVEFILE_DELAY_UNTIL_REBOOT */);
        }

        // ------------------------------------------------------------------ Processi in un Job object

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int size, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(string app, StringBuilder cmdLine, IntPtr pa, IntPtr ta, bool inherit,
            uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        private const uint CREATE_SUSPENDED = 0x4;

        /// <summary>
        /// Avvia una riga di comando dentro un Job object: così sappiamo quando sono terminati
        /// anche i processi figli (molti disinstallatori si copiano in %TEMP% e si rilanciano).
        /// </summary>
        public sealed class JobProcess : IDisposable
        {
            private IntPtr job;
            public int ProcessId { get; private set; }

            public static JobProcess Start(string commandLine, string workingDir)
            {
                var jp = new JobProcess();
                jp.job = CreateJobObject(IntPtr.Zero, null);
                if (jp.job == IntPtr.Zero) throw new Win32Exception();
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                PROCESS_INFORMATION pi;
                if (workingDir != null && !Directory.Exists(workingDir)) workingDir = null;
                if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                        CREATE_SUSPENDED, IntPtr.Zero, workingDir, ref si, out pi))
                {
                    int err = Marshal.GetLastWin32Error();
                    CloseHandle(jp.job);
                    throw new Win32Exception(err);
                }
                AssignProcessToJobObject(jp.job, pi.hProcess); // se fallisce, contiamo comunque sul processo principale
                ResumeThread(pi.hThread);
                jp.ProcessId = pi.dwProcessId;
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return jp;
            }

            /// <summary>Numero di processi ancora vivi nel job (il disinstallatore e i suoi figli).</summary>
            public int ActiveProcesses
            {
                get
                {
                    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info;
                    if (job == IntPtr.Zero || !QueryInformationJobObject(job, 1, out info, Marshal.SizeOf(typeof(JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)), IntPtr.Zero))
                        return 0;
                    return (int)info.ActiveProcesses;
                }
            }

            public void Dispose()
            {
                if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            }
        }

        // ------------------------------------------------------------------ Percorso dei processi in esecuzione

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        public static string GetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static bool IsElevated
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        // ------------------------------------------------------------------ Collegamenti .lnk

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        /// <summary>Legge destinazione e argomenti di un file .lnk (senza risolverlo).</summary>
        public static bool ReadShortcut(string lnkPath, out string target, out string args)
        {
            target = null; args = null;
            object obj = null;
            try
            {
                obj = new CShellLink();
                ((IPersistFile)obj).Load(lnkPath, 0);
                var link = (IShellLinkW)obj;
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                target = Environment.ExpandEnvironmentVariables(sb.ToString());
                sb.Clear();
                link.GetArguments(sb, sb.Capacity);
                args = sb.ToString();
                return true;
            }
            catch { return false; }
            finally { if (obj != null) Marshal.ReleaseComObject(obj); }
        }

        // ------------------------------------------------------------------ Selettore cartelle moderno

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRCW { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filterSpec);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        /// <summary>Mostra il selettore di cartelle di Windows. Restituisce null se annullato.</summary>
        public static string PickFolder(IntPtr owner, string title, string initialDir)
        {
            var dlg = (IFileDialog)new FileOpenDialogRCW();
            try
            {
                dlg.SetOptions(0x20 | 0x40 | 0x800); // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                if (!string.IsNullOrEmpty(title)) dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                {
                    try
                    {
                        IShellItem folder;
                        SHCreateItemFromParsingName(initialDir, IntPtr.Zero, typeof(IShellItem).GUID, out folder);
                        dlg.SetFolder(folder);
                    }
                    catch { }
                }
                if (dlg.Show(owner) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out path);
                return path;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }

        // ------------------------------------------------------------------ Cartelle note

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        public static readonly Guid FolderSavedGames = new Guid("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");
        public static readonly Guid FolderLocalAppDataLow = new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16");
        public static readonly Guid FolderPublicDocuments = new Guid("ED4824AF-DCE4-45A8-81E2-FC7965083634");
        public static readonly Guid FolderPublicDesktop = new Guid("C4AA340D-F20F-4863-AFEF-F87EF2E6BA25");
        public static readonly Guid FolderScreenshots = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f");
        public static readonly Guid FolderCaptures = new Guid("EDC0FE71-98D8-4F4A-B920-C8DC133CB165");
        public static readonly Guid FolderDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        public static string GetKnownFolder(Guid id)
        {
            IntPtr p;
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }

        // ------------------------------------------------------------------ Barra del titolo scura

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void StyleTitleBar(IntPtr hwnd, int captionRgb, bool dark)
        {
            int on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            // COLORREF = 0x00BBGGRR (solo Windows 11)
            int colorref = ((captionRgb & 0xFF) << 16) | (captionRgb & 0xFF00) | ((captionRgb >> 16) & 0xFF);
            DwmSetWindowAttribute(hwnd, 35, ref colorref, 4);
            DwmSetWindowAttribute(hwnd, 34, ref colorref, 4);
            // il testo del titolo deve restare leggibile sul colore scelto (solo Windows 11)
            int text = dark ? 0x00F0EAE8 : 0x00221B17;
            DwmSetWindowAttribute(hwnd, 36, ref text, 4);
        }

        // ------------------------------------------------------------------ Decompressione (Prefetch)

        [DllImport("ntdll.dll")]
        private static extern uint RtlGetCompressionWorkSpaceSize(ushort format, out uint bufferWorkspace, out uint fragmentWorkspace);

        [DllImport("ntdll.dll")]
        private static extern uint RtlDecompressBufferEx(ushort format, byte[] output, int outputSize, byte[] input, int inputSize, out int finalSize, byte[] workspace);

        [DllImport("ntdll.dll")]
        private static extern uint RtlCompressBuffer(ushort format, byte[] input, int inputSize, byte[] output, int outputSize, uint chunkSize, out int finalSize, byte[] workspace);

        public const ushort XpressHuffman = 4;

        public static byte[] Decompress(ushort format, byte[] input, int offset, int outputSize)
        {
            uint ws, fs;
            if (RtlGetCompressionWorkSpaceSize(format, out ws, out fs) != 0) return null;
            var workspace = new byte[ws];
            var src = new byte[input.Length - offset];
            Buffer.BlockCopy(input, offset, src, 0, src.Length);
            var output = new byte[outputSize];
            int final;
            if (RtlDecompressBufferEx(format, output, outputSize, src, src.Length, out final, workspace) != 0) return null;
            return output;
        }

        /// <summary>Usato solo dai test per creare file Prefetch sintetici.</summary>
        public static byte[] Compress(ushort format, byte[] input)
        {
            uint ws, fs;
            RtlGetCompressionWorkSpaceSize(format, out ws, out fs);
            var output = new byte[input.Length + 4096];
            int final;
            if (RtlCompressBuffer(format, input, input.Length, output, output.Length, 4096, out final, new byte[ws]) != 0) return null;
            var result = new byte[final];
            Buffer.BlockCopy(output, 0, result, 0, final);
            return result;
        }

        // ------------------------------------------------------------------ Volumi

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformation(string root, StringBuilder volumeName, int volumeNameSize,
            out uint serial, out uint maxComponent, out uint flags, StringBuilder fsName, int fsNameSize);

        public static uint? GetVolumeSerial(string root)
        {
            uint serial, maxc, flags;
            if (GetVolumeInformation(root, null, 0, out serial, out maxc, out flags, null, 0)) return serial;
            return null;
        }

        // ------------------------------------------------------------------ Icone

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr large, out IntPtr small, uint size);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        /// <summary>Estrae l'icona di un eseguibile come immagine WPF (null se assente).</summary>
        public static System.Windows.Media.ImageSource ExtractIcon(string file, int size)
        {
            return ExtractIcon(file, 0, size);
        }

        /// <summary>
        /// Estrae un'icona indicata come in DisplayIcon ("C:\app.exe,2" o ",-101" per l'ID della risorsa).
        /// </summary>
        public static System.Windows.Media.ImageSource ExtractIcon(string file, int index, int size)
        {
            IntPtr large, small;
            if (string.IsNullOrEmpty(file) || SHDefExtractIcon(file, index, 0, out large, out small, (uint)(size | (16 << 16))) != 0 || large == IntPtr.Zero)
                return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(large, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
        }

        // ------------------------------------------------------------------ Nomi brevi 8.3

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetLongPathName(string shortPath, StringBuilder longPath, int size);

        /// <summary>Converte un percorso con nomi brevi ("C:\PROGRA~1\...") nel percorso completo; se non esiste lo lascia com'è.</summary>
        public static string GetLongPath(string path)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int n = GetLongPathName(path, sb, sb.Capacity);
                if (n > sb.Capacity) { sb = new StringBuilder(n + 1); n = GetLongPathName(path, sb, sb.Capacity); }
                if (n > 0 && n < sb.Capacity) return sb.ToString();
                // il file non esiste: si converte almeno la parte iniziale che esiste (es. "C:\PROGRA~1\Sparito")
                string parent = System.IO.Path.GetDirectoryName(path);
                if (parent != null && parent.Length < path.Length && parent.IndexOf('~') >= 0)
                    return System.IO.Path.Combine(GetLongPath(parent), System.IO.Path.GetFileName(path));
            }
            catch { }
            return path;
        }

        // ------------------------------------------------------------------ Collegamenti (junction, link simbolici) e percorsi reali

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFile(string fileName, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr handle);

        /// <summary>Percorso utilizzabile dalle API di Windows anche oltre i 260 caratteri.</summary>
        public static string ExtendedPath(string path)
        {
            return PathUtil.Long(path);
        }

        /// <summary>
        /// True se il percorso è un collegamento verso un'altra posizione (junction, link simbolico, punto di montaggio):
        /// sono i reparse point "sostituti di nome". Quelli di OneDrive (file su richiesta) o della deduplicazione
        /// sono file e cartelle veri e non contano.
        /// </summary>
        public static bool IsLink(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            WIN32_FIND_DATA d;
            IntPtr h = FindFirstFile(ExtendedPath(path.TrimEnd('\\')), out d);
            if (h == new IntPtr(-1))
            {
                // non leggibile: nel dubbio vale l'attributo (un collegamento non seguito è sempre la scelta prudente)
                try { return (System.IO.File.GetAttributes(PathUtil.Long(path)) & System.IO.FileAttributes.ReparsePoint) != 0; }
                catch { return false; }
            }
            FindClose(h);
            if ((d.dwFileAttributes & 0x400) == 0) return false; // FILE_ATTRIBUTE_REPARSE_POINT
            return (d.dwReserved0 & 0x20000000) != 0;              // IsReparseTagNameSurrogate
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, int size, int flags);

        /// <summary>
        /// Posizione reale di un file o cartella, dopo aver seguito tutti i collegamenti del percorso
        /// (null se non esiste o non si può aprire). Le condivisioni di rete tornano come "\\server\condivisione\...".
        /// </summary>
        public static string FinalPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            IntPtr h = CreateFile(ExtendedPath(path), 0, 7 /* FILE_SHARE_READ | WRITE | DELETE */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
                if (n >= sb.Capacity) { sb = new StringBuilder(n + 1); n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0); }
                if (n <= 0 || n >= sb.Capacity) return null;
                string s = sb.ToString();
                if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);
                if (s.StartsWith(@"\\?\")) s = s.Substring(4);
                return s.Length > 3 ? s.TrimEnd('\\') : s;
            }
            finally { CloseHandle(h); }
        }

        // ------------------------------------------------------------------ Testi delle app dello Store

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);

        /// <summary>Risolve un testo indiretto ("@{Pacchetto?ms-resource://...}") nella lingua di Windows.</summary>
        public static string LoadIndirectString(string source)
        {
            var sb = new StringBuilder(1024);
            return SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
        }

        // ------------------------------------------------------------------ Variabili d'ambiente

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        /// <summary>Porta una finestra in primo piano (ripristinandola se è ridotta a icona).</summary>
        /// <summary>
        /// Chiede a Esplora risorse di aprire un file o un indirizzo: parte con i suoi privilegi (non da amministratore)
        /// e nella cartella indicata. Passare il percorso a explorer.exe non basta: il programma partirebbe in system32
        /// e molti giochi non troverebbero i loro file.
        /// </summary>
        public static bool ShellExecuteViaExplorer(string file, string directory)
        {
            try
            {
                dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
                object location = 0; // CSIDL_DESKTOP
                object root = Type.Missing;
                int hwnd;
                dynamic desktop = windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out hwnd, 1 /* SWFO_NEEDDISPATCH */);
                desktop.Document.Application.ShellExecute(file, "", directory ?? "", "open", 1);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Avvio tramite Esplora risorse non riuscito: {0}", ex.Message);
                return false;
            }
        }

        public static void BringToFront(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            SetForegroundWindow(hwnd);
        }

        public static void Foreground(IntPtr hwnd)
        {
            SetForegroundWindow(hwnd);
        }

        // ------------------------------------------------------------------ Sorveglianza in background

        internal const int RegNotifyName = 0x1, RegNotifyLastSet = 0x4;

        [DllImport("advapi32.dll")]
        internal static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree, int filter,
            Microsoft.Win32.SafeHandles.SafeWaitHandle evt, bool asynchronous);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        /// <summary>
        /// Vero se ora un avviso non disturba: no con un gioco o un'app a schermo intero, una presentazione,
        /// lo schermo bloccato o il salvaschermo (allora l'avviso aspetta).
        /// </summary>
        public static bool CanShowNotification
        {
            get
            {
                int state;
                try { if (SHQueryUserNotificationState(out state) != 0) return true; }
                catch { return true; }
                return state == 5 /* QUNS_ACCEPTS_NOTIFICATIONS */ || state == 7 /* QUNS_APP */;
            }
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        /// <summary>Un avviso non prende il fuoco a ciò che si sta facendo e non compare in Alt+Tab.</summary>
        public static void MakeNotificationWindow(IntPtr hwnd)
        {
            const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
            long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }

        /// <summary>Avvisa Esplora risorse e le altre app che le variabili d'ambiente sono cambiate.</summary>
        public static void BroadcastEnvironmentChange()
        {
            UIntPtr result;
            SendMessageTimeout(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, UIntPtr.Zero, "Environment",
                0x0002 /* SMTO_ABORTIFHUNG */, 3000, out result);
        }
    }

    /// <summary>Il Cestino non ha accettato l'elemento (che quindi è ancora al suo posto).</summary>
    internal sealed class RecycleBinException : IOException
    {
        public RecycleBinException(string reason) : base(L.F("Impossibile spostare nel Cestino: {0}", reason)) { }
    }
}
