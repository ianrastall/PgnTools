@workspace /PgnTools.Wpf/App.xaml.cs /PgnTools.Wpf/MainWindow.xaml /PgnTools.Wpf/MainWindow.xaml.cs

I need to implement the Main Window UI for PgnTools V5.
The backend logic in `App.xaml.cs` (LoadFile, CurrentSession) is already written.
Now I need the visual shell to connect to it.

**TASK:** Rewrite `MainWindow.xaml` and `MainWindow.xaml.cs` to create the "Pro" Chess Database Layout.

**REQUIREMENTS for MainWindow.xaml:**

1.  **Layout:** Use a Grid with 3 Rows:
    - Row 0: Top Menu + Toolbar (StackPanel).
    - Row 1: The DataGrid (occupies remaining space).
    - Row 2: Status Bar.
2.  **DataGrid:**
    - Name it `GameGrid`.
    - Set `IsReadOnly="True"` and `AutoGenerateColumns="False"`.
    - Enable Virtualization: `VirtualizingPanel.IsVirtualizing="True"` and `VirtualizingPanel.VirtualizationMode="Recycling"`.
    - **Columns:** Create columns bound strictly to the 32-byte struct fields:
      - "White" (WhiteNameId)
      - "Black" (BlackNameId)
      - "W-Elo" (WhiteElo)
      - "B-Elo" (BlackElo)
      - "Date" (DateCompact)
3.  **Styling:** Use the `<Button Style="{StaticResource ProButtonStyle}" ... />` for the Toolbar buttons. (Do NOT use `ui:Button`).

**REQUIREMENTS for MainWindow.xaml.cs:**

1.  **Open File Logic:**
    - Handle the "Open" menu click.
    - Use `Microsoft.Win32.OpenFileDialog` to pick a `.pgn` file.
    - Call `App.LoadFile(path)`.
    - If successful, assign `GameGrid.ItemsSource = App.CurrentGameList;` directly.
    - Update a Status Bar TextBlock with the file name and game count (`App.CurrentGameList.Count`).
2.  **Exit Logic:** Call `Application.Current.Shutdown()`.

**CONSTRAINT:** Do not create a ViewModel for the Window. Use Code-Behind for these simple shell interactions as per the V5 Architecture.

Generate the code for both files now.
