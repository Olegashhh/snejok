# Winter Desktop — технологический стек

Статус: **актуально для MVP (этапы 1–2 ТЗ)**.
Последнее обновление: завершение MVP.

Ограничения выбора: только бесплатные библиотеки с открытым исходным кодом, без
Electron/браузерной оболочки, без обязательной сети и телеметрии (§1.2, §2, §7 ТЗ).

## 1. Платформа и рантайм

| Компонент | Версия / требования | Зачем |
|---|---|---|
| ОС | Windows 10 64-bit, Windows 11 64-bit | DirectComposition и flip-model swapchain доступны начиная с Windows 8/10; проверено на Windows 11 |
| .NET | 8.0 (LTS), SDK 8.0.428 | Основной рантайм; целевые TFM: `net8.0`, `net8.0-windows` |
| C# | 12 | `LangVersion` 12, nullable reference types включены |
| WPF | в составе .NET 8 Desktop Runtime | Окно настроек: быстрый старт, биндинги, знакомый вид Windows |
| WinForms | в составе .NET 8 Desktop Runtime | Только `NotifyIcon` для системного трея (у WPF нет штатного трея) |
| PowerShell | 5.1+ | Вспомогательный скрипт `tools/make-icon.ps1`; не требуется для запуска |

Сборка: `dotnet build WinterDesktop.sln -c Debug`. Для работы приложения нужен
**.NET Desktop Runtime 8 x64**, для сборки — **.NET SDK 8**.

## 2. Графика

| Библиотека | Версия | Лицензия | Роль |
|---|---|---|---|
| Vortice.Direct3D11 | 3.8.3 | MIT | Создание D3D11-устройства, текстур, контекста |
| Vortice.DXGI | 3.8.3 | MIT | Flip-model swapchain (`CreateSwapChainForComposition`), адаптеры, WARP |
| Vortice.Direct2D1 | 3.8.3 | MIT | Отрисовка снежинок и сугробов: спрайт-батчи, меши, геометрия |
| Vortice.DirectComposition | 3.8.3 | MIT | Подключение swapchain к прозрачному окну без redirection bitmap |
| Vortice.Mathematics | 2.1.1 | MIT | Векторы, матрицы, цвета |

Почему Vortice: тонкие, но полноценные обёртки над нативными COM-интерфейсами,
MIT, активная поддержка .NET 8, без скрытого рендер-цикла и без тяжёлой
платформенной надстройки. Альтернативы (SharpDX — заброшен; Silk.NET — более
низкоуровневый и шумный для D2D; Win2D — WinRT/UWP-ориентирован) отклонены.

Ключевые технические решения:

- Один прозрачный `WS_POPUP` HWND на монитор,
  `WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW |
  WS_EX_NOACTIVATE`. `WS_EX_LAYERED` обязателен для click-through через
  `WS_EX_TRANSPARENT`; прозрачность содержимого обеспечивает premultiplied alpha.
- `CreateSwapChainForComposition` с `AlphaMode = Premultiplied` + DirectComposition
  visual → аппаратная композиция без копирования кадра через GDI.
- Direct2D-таргет с `Dpi = 96`: все координаты — физические пиксели.
- Один поток эффектов владеет окнами, устройствами и симуляцией; UI общается с ним
  через неизменяемые снимки и очередь команд.
- WARP — автоматический fallback, если аппаратный D3D11-адаптер недоступен.

## 3. Системная интеграция (Win32)

Собственные P/Invoke-объявления в `WinterDesktop.Platform` (без сторонних обёрток):

| API | Назначение |
|---|---|
| `SetWinEventHook` (`EVENT_OBJECT_CREATE..LOCATIONCHANGE`, `EVENT_SYSTEM_FOREGROUND`) | Отслеживание появления, перемещения и z-order окон |
| `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`, `DWMWA_CLOAKED` | Точная геометрия видимых границ, пропуск cloaked-окон |
| `GetWindowRect`, `IsWindowVisible`, `IsIconic`, `GetClassNameW`, `GetWindowLongPtrW` | Фильтрация окон: tool windows, невидимые, свёрнутые |
| `EnumWindows` | Порядок перечисления задаёт приблизительный z-order (сверху вниз) |
| `EnumDisplayMonitors`, `GetMonitorInfoW`, DPI API | Мониторы, рабочие области, масштаб |
| `GetCursorPos`, `GetAsyncKeyState` | Позиция курсора и состояние кнопки без глобального хука |
| `GetSystemPowerStatus` | Определение работы от батареи |
| `SetWindowLongPtrW` / `SetWindowPos` | Click-through (`WS_EX_LAYERED` + `WS_EX_TRANSPARENT`) и topmost-слой |
| `SetLayeredWindowAttributes` | `LWA_ALPHA = 255`: слоёное окно без визуальных изменений |
| `RegisterHotKey` / `UnregisterHotKey` | Глобальная Ctrl+Alt+W: аварийный выход из режима взаимодействия |
| `RegOpenKeyExW` / `RegSetValueExW` / `RegDeleteValueW` | Автозапуск через `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

Никаких драйверов, служб и внедрения в чужие процессы.

## 4. Хранение и данные

| Технология | Роль |
|---|---|
| `System.Text.Json` (в составе .NET 8) | Сериализация настроек, импорт/экспорт |
| Собственный `SettingsStore` | Debounce 0,7 с, атомарная запись `File.Replace` + `.bak`, восстановление `.bak`, сохранение копии повреждённого JSON |
| Собственный `FileLog` | Логи `%LOCALAPPDATA%\WinterDesktop\logs`, ротация по дням, 7 файлов, лимит 4 МБ на файл |
| `%LOCALAPPDATA%\WinterDesktop\assets\` | Зарезервировано под пользовательские изображения (этап 3) |

Схема настроек версионируется (`SchemaVersion = 1`). Миграции появятся вместе с
первым изменением схемы; до этого загрузка валидирует и дополняет отсутствующие
поля значениями по умолчанию.

## 5. Тестирование

| Инструмент | Версия | Лицензия |
|---|---|---|
| xUnit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| coverlet.collector | 6.0.2 | MIT |

57 тестов: 45 — ядро/снег/геометрия/настройки модели, 12 — хранилище настроек
(включая битый JSON, `.bak`, debounce, импорт/экспорт). GPU и реальные окна в
юнит-тестах не нужны.

Ручная/интеграционная проверка MVP выполнялась через UI Automation: переключение
режимов, ползунки, трей, мониторы, автозапуск, сохранение между запусками,
click-through (проверка extended style окна), скриншоты слоя.

## 6. Инструменты разработки

| Инструмент | Роль |
|---|---|
| `dotnet` CLI | Сборка, тесты, публикация |
| Visual Studio 2022 / VS Code | Опционально; проект собирается без IDE |
| `tools/make-icon.ps1` | Генерация `app.ico` без сторонних библиотек |
| UI Automation (PowerShell) | Приёмочная проверка интерфейса и overlay-окон |

## 7. Что не используется и почему

| Технология | Причина отказа |
|---|---|
| Electron / WebView2 / браузерная оболочка | Запрещено ТЗ; лишний вес и память |
| WPF `AllowsTransparency` для overlay | Программный рендер, не тянет полноэкранный снег на 60 FPS |
| `WS_EX_LAYERED` + `UpdateLayeredWindow` | Копирование кадра через CPU/GDI каждый кадр |
| WinUI 3 / Windows App SDK | Тяжёлый деплой, избыточен для одного окна настроек и трея |
| Глобальный хук мыши (`SetWindowsHookEx`) | Не нужен: курсор опрашивается раз в кадр; хук менее приватный и менее надёжный |
| Чтение содержимого окон (`GetWindowText`, скриншоты, OCR) | Запрещено ТЗ; используются только прямоугольники |
| Платные SDK, подписки, сетевые сервисы | Запрещено ТЗ |
| SkiaSharp | Допустим ТЗ, но не нужен: D2D уже даёт аппаратные спрайты и геометрию |

## 8. Планируемое на следующих этапах

| Этап | Технологии |
|---|---|
| 3. Снежки, снеговики, гирлянды, снегоуборщик, свои изображения | D2D-геометрия и спрайты, `WIC`/`System.Drawing` для декодирования пользовательских PNG/JPEG, расширение `SnowEngine` |
| 4. Живые обои и сцены | `WorkerW`-трюк Explorer с fallback, анимация спрайтов |
| 5. Оптимизация | Длительные нагрузочные прогоны, профилирование памяти, 4K/несколько мониторов, сон/пробуждение |
| 6. Релиз | Inno Setup (бесплатный) или MSIX, подпись, GitHub Release |

## 9. Лицензии сторонних компонентов

| Компонент | Лицензия |
|---|---|
| .NET 8, WPF, WinForms, System.Text.Json | MIT |
| Vortice.* | MIT |
| xUnit | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

Все зависимости бесплатны и не требуют регистрации или сетевого доступа во время
работы приложения.
