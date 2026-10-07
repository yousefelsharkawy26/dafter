# AGENTS.md

Instructions for AI coding agents working in this repository: a cross-platform .NET desktop app built with **Avalonia UI** and **FluentAvalonia** (Fluent/WinUI-style controls and theme).

## 0. Before writing any code

1. Read `*.csproj`, `Directory.Build.props` and `Directory.Packages.props` to find:
   - the target framework (`net8.0`, `net9.0`, ...)
   - the Avalonia version (11.x expected)
   - the FluentAvalonia version (2.x pairs with Avalonia 11)
   - which MVVM library is used (CommunityToolkit.Mvvm or ReactiveUI)
2. Look at how existing views, view models and styles are written, and follow the same patterns. Consistency with the codebase beats personal preference.
3. Avalonia and FluentAvalonia APIs differ between versions. If you are not sure an API, property or control exists in the installed version, check the docs or the FluentAvalonia sample app (`FluentAvaloniaSamples` in the upstream repo) instead of guessing. Never invent control names or properties.

Docs:
- https://docs.avaloniaui.net
- https://github.com/amwx/FluentAvalonia (and its wiki)

## 1. This is Avalonia, not WPF

Do not write WPF/UWP code. Common mistakes to avoid:

| WPF habit | Avalonia way |
|---|---|
| `Visibility="Collapsed"` | `IsVisible="False"` |
| `.xaml` files | `.axaml` files |
| `Style TargetType` + `Triggers` / `DataTrigger` | CSS-like selectors: `<Style Selector="Button.accent:pointerover">`; use classes and pseudo-classes |
| `pack://application:,,,/Asm;component/x.png` | `avares://AssemblyName/Assets/x.png` |
| `Dispatcher.Invoke` | `Dispatcher.UIThread.InvokeAsync` / `Post` |
| `OpenFileDialog` / `SaveFileDialog` | `TopLevel.GetTopLevel(this)!.StorageProvider` (`OpenFilePickerAsync`, `SaveFilePickerAsync`, `OpenFolderPickerAsync`) |
| `Clipboard.SetText` | `TopLevel.GetTopLevel(this)?.Clipboard` async API |
| `MessageBox.Show` | FluentAvalonia `ContentDialog` or `TaskDialog` |
| `DependencyProperty` | `StyledProperty<T>` / `DirectProperty<,>` registered with `AvaloniaProperty.Register` |
| `RowDefinitions="..."` as elements only | short string form works: `ColumnDefinitions="Auto,*"` |

Other rules:
- Do not reference `System.Windows.*`, `PresentationFramework` or any Windows-only API in UI code.
- Use `x:DataType` and compiled bindings. Prefer `x:CompileBindings="True"` (or `AvaloniaUseCompiledBindingsByDefault`) so binding errors fail at build time.
- Use `{Binding}` with classic reflection bindings only if the project already does so.

## 2. FluentAvalonia specifics

- The theme is enabled in `App.axaml` through `FluentAvaloniaTheme`. Do not add a second Fluent theme.
- Namespace for controls: `xmlns:ui="using:FluentAvalonia.UI.Controls"`.
- Key controls: `NavigationView`, `ContentDialog`, `TaskDialog`, `InfoBar`, `SettingsExpander`, `NumberBox`, `ColorPicker`, `FAComboBox`, `FAMenuFlyout`, `TeachingTip`, `ProgressRing`.
- In 2.x some controls were renamed with an `FA` prefix. Verify the exact name and namespace in the installed version before using a control.
- For a custom title bar or Mica/Acrylic window, use `AppWindow` (from `FluentAvalonia.UI.Windowing`) instead of building one by hand.
- Navigation: use `NavigationView` with the project's navigation service (if any) rather than instantiating views directly.

### Theming and colors
- Never hardcode colors or font sizes in views.
- Use `{DynamicResource ...}` with theme keys so light/dark/high-contrast work, for example:
  - text: `TextFillColorPrimaryBrush`, `TextFillColorSecondaryBrush`
  - surfaces: `CardBackgroundFillColorDefaultBrush`, `SolidBackgroundFillColorBaseBrush`
  - accent: `SystemAccentColor`, `AccentFillColorDefaultBrush`
  - borders: `CardStrokeColorDefaultBrush`, `ControlStrokeColorDefaultBrush`
- Use the built-in type ramp styles (`Classes="TitleTextBlockStyle"`, `BodyTextBlockStyle`, `SubtitleTextBlockStyle`, ...) instead of custom sizes.
- Icons: use the icon system already used in the project (for example `FluentIcons.Avalonia`, `Projektanker.Icons.Avalonia` or `SymbolIcon`). Do not add a new icon package without asking.
- Test any new UI in both Light and Dark theme.

## 3. Architecture

- Follow MVVM with the library already in the project. **Never mix** CommunityToolkit.Mvvm and ReactiveUI.
  - CommunityToolkit: `[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`.
  - ReactiveUI: `ReactiveObject`, `ReactiveCommand`, `WhenAnyValue`.
- Keep code-behind minimal (view-only concerns such as focus, pickers needing a `TopLevel`, animations). Business logic belongs in view models and services.
- View models must not reference controls, `Window` or other view types. For dialogs, file pickers and navigation, go through an injected service/interface.
- Use the existing DI container. Register new services and view models in the same place as existing ones. Do not use service locators or static singletons.
- Long-running work must be async and must not block the UI thread. Marshal back with `Dispatcher.UIThread` only when needed.
- Dispose subscriptions and unsubscribe from events to avoid leaks (especially with `ReactiveUI`/`IObservable`).
- Prefer `ItemsControl`/`ItemsRepeater` with `DataTemplate`s and virtualized panels for lists. Avoid building UI elements in code.

## 4. Cross-platform rules

The app targets Windows, Linux and macOS.
- Build paths with `Path.Combine`; never hardcode `\` or drive letters.
- Store settings in the proper per-user folder (`Environment.SpecialFolder.ApplicationData` / `LocalApplicationData`).
- Do not rely on Windows-only fonts, registry, P/Invoke or COM without a `RuntimeInformation.IsOSPlatform` guard and a fallback.
- Mica/Acrylic and some `AppWindow` features are platform-dependent. Always provide a graceful fallback.
- Keep file name casing exact (Linux is case-sensitive), including `avares://` asset paths.

## 5. Workflow

- Make small, focused changes. After each one run `dotnet build`. XAML errors (including compiled binding errors) show up at build time.
- Fix all new warnings you introduce. Do not suppress warnings to make the build pass.
- Run `dotnet test` if tests exist. For UI tests use `Avalonia.Headless` (xUnit/NUnit); do not require a real display.
- Do not add or upgrade NuGet packages without asking first, and never change Avalonia/FluentAvalonia versions on your own.
- Do not reformat unrelated files or rename things outside the scope of the task.
- Match the existing code style (`.editorconfig` is the source of truth), including naming, nullable annotations and file-scoped namespaces.
- Avoid `async void` except for event handlers. Pass `CancellationToken` through async APIs where the codebase does.
- When unsure about requirements, ask before making large structural changes.

## 6. Quick checklist before finishing

- [ ] `dotnet build` passes with no new warnings
- [ ] No WPF-only APIs or `Visibility`/`DataTrigger`/`pack://` leftovers
- [ ] `x:DataType` set on every new view/DataTemplate
- [ ] No hardcoded colors; theme resources used via `DynamicResource`
- [ ] Looks right in Light and Dark theme
- [ ] Logic lives in the view model/service, not in code-behind
- [ ] New services/view models registered in DI
- [ ] No unrequested packages or version bumps
- [ ] Works conceptually on Windows, Linux and macOS (paths, fonts, native calls)