# Плагины BagaGame

Лаунчер поддерживает внешние плагины. Плагин может добавить свои вкладки в левую навигацию и построить любой WPF-интерфейс внутри вкладки. Плагины не входят в сборку лаунчера и не зависят от его версии.

## Куда класть плагины

```
%LOCALAPPDATA%\BagaGame\Plugins
```

На этом компьютере:

```
C:\Users\BAGA\AppData\Local\BagaGame\Plugins
```

Папка создаётся автоматически. После установки плагина перезапустите лаунчер. Открыть папку можно в `Настройки → Плагины → Открыть папку`.

Поддерживаются два варианта:

1. Папка плагина с манифестом (рекомендуется, с зависимостями):

```
Plugins\
  HelloPlugin\
    plugin.json
    HelloPlugin.dll
    <зависимости>.dll
```

2. Один DLL прямо в `Plugins` (без зависимостей).

## Манифест plugin.json

```json
{
  "id": "sample.hello",
  "name": "Hello Plugin",
  "version": "1.0.0",
  "apiVersion": 1,
  "assembly": "HelloPlugin.dll"
}
```

- `id` — уникальный идентификатор плагина.
- `assembly` — DLL относительно папки плагина.
- `apiVersion` — версия контракта, под которую собран плагин.
- Остальные поля нужны для диагностики.

## Требования

- .NET Framework 4.7.2.
- WPF (`UseWPF`).
- Ссылка только на `BagaGame.PluginApi.dll`. Не ссылайтесь на `BagaGame.exe`: API специально вынесен в отдельную сборку.
- Не блокируйте UI-поток в `Initialize`.

## Создание проекта

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <UseWPF>true</UseWPF>
    <AssemblyName>HelloPlugin</AssemblyName>
    <RootNamespace>HelloPlugin</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\BagaGame.PluginApi\BagaGame.PluginApi.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
```

`Private=false` обязателен: контракт предоставляет сам лаунчер. Если вы не собираете API из исходников, укажите `<Reference Include="BagaGame.PluginApi">` с путём к `BagaGame.PluginApi.dll`.

## Минимальный плагин

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using BagaGame.PluginApi;

namespace HelloPlugin;

public sealed class HelloPlugin : IBagaGamePlugin
{
    private IPluginHost? _host;

    public string Id => "sample.hello";
    public string Name => "Hello Plugin";
    public Version Version => new(1, 0, 0);
    public int ApiVersion => PluginApiInfo.Version;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.AddTab(new PluginTab(
            id: "hello",
            title: "Hello",
            createContent: CreatePage,
            order: 10));
    }

    private FrameworkElement CreatePage()
    {
        var root = new StackPanel { Margin = new Thickness(36, 26, 36, 26) };

        root.Children.Add(new TextBlock
        {
            Text = "Hello from a plugin",
            FontSize = 30,
            FontWeight = FontWeights.SemiBold,
        });

        var button = new Button
        {
            Content = "Click me",
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(18, 8, 18, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => MessageBox.Show("Plugin works!");
        root.Children.Add(button);

        return root;
    }

    public void Shutdown() { }
}
```

## API

### IBagaGamePlugin

| Член | Назначение |
|---|---|
| `Id` | Уникальный идентификатор. Дубликаты игнорируются. |
| `Name` | Имя для лога. |
| `Version` | Версия плагина. |
| `ApiVersion` | Версия контракта, с которой собран плагин. |
| `Initialize(IPluginHost)` | Регистрация вкладок. Вызывается один раз. |
| `Shutdown()` | Освобождение ресурсов при выходе. |

### IPluginHost

| Член | Назначение |
|---|---|
| `DataDirectory` | `%LOCALAPPDATA%\BagaGame` |
| `PluginDirectory` | Папка, из которой загружен плагин |
| `Dispatcher` | UI-диспетчер лаунчера |
| `Language` | `ru` или `en` |
| `LanguageChanged` | Событие смены языка |
| `AddTab(PluginTab)` | Добавить вкладку |
| `RemoveTab(string)` | Убрать вкладку |
| `UpdateTab(id, title, icon)` | Обновить название или иконку |
| `Log(string)` | Запись в `plugin.log` |

### PluginTab

```csharp
new PluginTab(
    id: "my-tab",
    title: "Моя вкладка",
    createContent: CreatePage,
    icon: null,
    order: 100);
```

- `CreateContent` вызывается **один раз** при первом открытии вкладки, на UI-потоке.
- Результат кэшируется. Возвращайте `FrameworkElement`.
- `order` задаёт порядок вкладок в навигации.
- `icon` — `ImageSource`; если не задан, используется стандартная иконка.

## UI в стиле лаунчера

`IPluginHost.Ui` возвращает набор готовых элементов, которые используют те же шрифты, отступы, карточки и цвета, что и остальное приложение. Ничего не нужно копировать из стилей лаунчера.

```csharp
public void Initialize(IPluginHost host)
{
    host.AddTab(new PluginTab("demo", "Демо", () => BuildPage(host.Ui)));
}

private FrameworkElement BuildPage(IPluginUi ui)
{
    var page = ui.Page();

    page.Children.Add(ui.PageTitle("Моя вкладка"));
    page.Children.Add(ui.PageSubtitle("Короткое описание раздела."));
    page.Children.Add(ui.SectionTitle("НАСТРОЙКИ"));

    var card = new StackPanel();
    card.Children.Add(ui.Body("Текст внутри карточки."));

    var button = ui.PrimaryButton("Сохранить");
    button.Margin = new Thickness(0, 14, 0, 0);
    button.HorizontalAlignment = HorizontalAlignment.Left;
    card.Children.Add(button);

    page.Children.Add(ui.Card(card));
    return page;
}
```

| Метод | Результат |
|---|---|
| `Page()` | `StackPanel` с полями `36,26,36,26` |
| `PageTitle(text)` | крупный заголовок, как «Библиотека» |
| `PageSubtitle(text)` | серый текст под заголовком |
| `SectionTitle(text)` | маленький заголовок секции |
| `Body(text)` | основной текст с переносами |
| `Card(content)` | скруглённая карточка с фоном и обводкой |
| `PrimaryButton(text)` | зелёная кнопка действия |
| `SecondaryButton(text)` | тихая кнопка с обводкой |

### Ресурсы темы в XAML

Если вкладка собирается в XAML, используйте `DynamicResource` с этими ключами. Они есть и в тёмной, и в светлой теме и обновляются при переключении.

| Ключ | Назначение |
|---|---|
| `WindowTintBrush` | фон страницы |
| `CardFillBrush` | фон карточки |
| `CardStrokeBrush` | обводка карточки |
| `DividerBrush` | разделители |
| `TileFillBrush` | фон вторичной кнопки/плитки |
| `TextPrimaryBrush` | основной текст |
| `TextSecondaryBrush` | вторичный текст |
| `TextTertiaryBrush` | подписи |
| `AccentBrush` | зелёный акцент |
| `AccentForegroundBrush` | текст на акценте |
| `AccentSoftBrush` | мягкая заливка акцента |
| `AccentStrokeBrush` | обводка акцента |

Пример XAML:

```xml
<StackPanel Margin="36,26,36,26">
  <TextBlock Text="Моя вкладка"
             FontSize="30"
             FontWeight="SemiBold"
             Foreground="{DynamicResource TextPrimaryBrush}" />
  <TextBlock Text="Описание"
             Margin="1,6,0,0"
             Foreground="{DynamicResource TextSecondaryBrush}" />

  <Border Margin="0,20,0,0"
          Padding="20"
          CornerRadius="10"
          BorderThickness="1"
          Background="{DynamicResource CardFillBrush}"
          BorderBrush="{DynamicResource CardStrokeBrush}">
    <TextBlock Text="Текст в карточке"
               TextWrapping="Wrap"
               Foreground="{DynamicResource TextSecondaryBrush}" />
  </Border>
</StackPanel>
```

Стандартные WPF-контролы наследуют шрифт лаунчера. Если задаёте цвета явно, используйте ключи выше, иначе в светлой теме текст может остаться белым.

## Версии и совместимость

- Контракт живёт в отдельной сборке `BagaGame.PluginApi` с фиксированной версией `1.0.0.0`.
- Лаунчер перехватывает запросы к `BagaGame.PluginApi` и всегда отдаёт свою копию. Плагин, собранный под API 1, продолжит работать после обновления лаунчера.
- `ApiVersion` проверяется при загрузке. Несовместимый плагин пропускается с записью в лог.
- Ломающие изменения контракта повышают `PluginApiInfo.Version`. Обратная совместимость внутри одной версии обязательна.

## Потоки и ошибки

- `Initialize` и `CreateContent` выполняются на UI-потоке. Долгую работу делайте в `Task.Run` и возвращайтесь через `host.Dispatcher`.
- Исключение в `Initialize` отключает только этот плагин.
- Исключение в `CreateContent` показывает страницу с ошибкой вместо падения лаунчера.
- Лог: `%LOCALAPPDATA%\BagaGame\plugin.log`.

## Установка

1. Соберите плагин в `Release`.
2. Скопируйте DLL и зависимости в `Plugins\MyPlugin\`.
3. Положите рядом `plugin.json`.
4. Перезапустите лаунчер.
5. Вкладка появится в разделе `ПЛАГИНЫ`.

## Пример

Готовый пример лежит в `docs/plugins/HelloPlugin`. Сборка:

```
dotnet build docs\plugins\HelloPlugin\HelloPlugin.csproj -c Release
```

Скопируйте `HelloPlugin.dll` и `plugin.json` в `%LOCALAPPDATA%\BagaGame\Plugins\HelloPlugin\`.

## Безопасность

Плагин выполняется с полными правами лаунчера: он может читать файлы, ходить в сеть и запускать процессы. Песочницы нет. Устанавливайте только те плагины, которым доверяете.
