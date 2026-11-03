using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AppManager
{
    /// <summary>
    /// Dialogo che gestisce da sé Esc e Invio. Restituisce true se il tasto è stato usato.
    /// Invio non deve mai confermare un'azione distruttiva: quella si conferma solo con un clic.
    /// </summary>
    internal interface IDialogKeys
    {
        bool OnEscape();
        bool OnEnter();
    }

    /// <summary>
    /// Scorciatoie della finestra principale e focus dei dialoghi:
    /// Ctrl+F cerca, F5 aggiorna, Canc disinstalla l'elemento selezionato (apre solo la conferma),
    /// Esc chiude il dialogo o svuota la ricerca, Invio conferma i dialoghi non distruttivi.
    /// </summary>
    internal static class KeyboardShortcuts
    {
        public static void Attach(Window window, MainViewModel vm)
        {
            window.PreviewKeyDown += (s, e) => OnPreviewKeyDown(window, vm, e);
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != "Dialog") return;
                // a dialogo aperto il focus va dentro il dialogo (la pagina dietro è disabilitata);
                // alla chiusura torna alla pagina, altrimenti la tastiera resterebbe "senza destinazione"
                window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    var host = (vm.HasDialog ? window.FindName("DialogHost") : window.FindName("PageHost")) as FrameworkElement;
                    if (host == null) return;
                    if (vm.HasDialog)
                    {
                        // il focus va sul dialogo senza evidenziare un pulsante: Tab porta al primo controllo, Esc e Invio funzionano subito
                        host.Focus();
                        return;
                    }
                    var list = FindChild<ListBox>(host, l => l.IsVisible && l.SelectedItem != null);
                    if (list != null) FocusSelected(list);
                    else host.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }));
            };
        }

        private static void OnPreviewKeyDown(Window window, MainViewModel vm, KeyEventArgs e)
        {
            // Ctrl+Alt è AltGr sulla tastiera italiana (@, #, [ ...): non è una scorciatoia
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Alt) != 0) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (vm.HasDialog)
            {
                if (e.IsRepeat || mods != ModifierKeys.None) return;
                var keys = vm.Dialog as IDialogKeys;
                if (keys == null) return;
                // Invio su un pulsante o una casella lascia fare al controllo (es. premere il pulsante con il focus)
                if (key == Key.Escape && keys.OnEscape()) e.Handled = true;
                else if (key == Key.Enter && !(e.OriginalSource is ButtonBase) && !(e.OriginalSource is TextBox) && keys.OnEnter()) e.Handled = true;
                return;
            }

            var page = window.FindName("PageHost") as FrameworkElement;
            bool inText = e.OriginalSource is TextBoxBase || e.OriginalSource is PasswordBox;

            if (key == Key.F && mods == ModifierKeys.Control)
            {
                var box = page == null ? null : FindSearchBox(page);
                if (box != null) { box.Focus(); box.SelectAll(); e.Handled = true; }
                return;
            }
            if (mods != ModifierKeys.None) return;

            if (key == Key.Escape && inText)
            {
                var box = e.OriginalSource as TextBox;
                if (box != null && box.Style == box.TryFindResource("SearchBox") && box.Text.Length > 0)
                {
                    box.Clear();
                    e.Handled = true;
                }
                return;
            }
            if (e.IsRepeat) return;
            if (key == Key.F5)
            {
                var cmd = PageCommand(vm.CurrentPage, "RefreshCommand") ?? PageCommand(vm.CurrentPage, "ScanCommand");
                if (cmd != null && cmd.CanExecute(null)) { cmd.Execute(null); e.Handled = true; }
                return;
            }
            if (key == Key.Delete && !inText && IsInsideListItem(e.OriginalSource as DependencyObject))
            {
                var library = vm.CurrentPage as LibraryPageViewModel;
                if (library != null && library.UninstallCommand.CanExecute(null))
                {
                    library.UninstallCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }

        private static ICommand PageCommand(object page, string name)
        {
            if (page == null) return null;
            var p = page.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(page, null) as ICommand;
        }

        private static TextBox FindSearchBox(DependencyObject root)
        {
            object style = null;
            var fe = root as FrameworkElement;
            if (fe != null) style = fe.TryFindResource("SearchBox");
            return FindChild<TextBox>(root, t => t.IsVisible && t.IsEnabled && (t.Style == style || t.Name == "Search"));
        }

        private static bool IsInsideListItem(DependencyObject d)
        {
            while (d != null)
            {
                if (d is ListBoxItem) return true;
                if (d is Window) return false;
                d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        private static void FocusSelected(ListBox list)
        {
            var item = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) as ListBoxItem;
            if (item != null) item.Focus();
            else list.Focus();
        }

        internal static T FindChild<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
        {
            if (parent == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var t = child as T;
                if (t != null && match(t)) return t;
                var found = FindChild(child, match);
                if (found != null) return found;
            }
            return null;
        }
    }
}
