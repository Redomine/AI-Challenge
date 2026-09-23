using System.Text;
using System.Text.RegularExpressions;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public enum ToolCapabilityScope
{
    General,
    View,
    Element
}

public sealed record ToolCapability(
    string ToolName,
    string Title,
    string Description,
    string Category,
    ToolCapabilityScope Scope,
    bool IsWrite,
    string Example);

public static class ToolCapabilityCatalog
{
    private static readonly IReadOnlyDictionary<string, ToolCapability> Known =
        new Dictionary<string, ToolCapability>(StringComparer.Ordinal)
        {
            ["workspace_list_files"] = Read("workspace_list_files", "Показать файлы проекта", "Перечисляет файлы внутри рабочего репозитория без чтения содержимого.", "Рабочий проект", ToolCapabilityScope.General, "Покажи C#-файлы проекта"),
            ["workspace_read_text_file"] = Read("workspace_read_text_file", "Прочитать файл проекта", "Читает небольшой текстовый UTF-8 файл внутри рабочего репозитория; секретные и бинарные файлы запрещены.", "Рабочий проект", ToolCapabilityScope.General, "Прочитай DesignerAssistant/Program.cs"),
            ["workspace_search_text"] = Read("workspace_search_text", "Найти текст в проекте", "Ищет строку в небольших текстовых файлах рабочего репозитория.", "Рабочий проект", ToolCapabilityScope.General, "Найди использования TaskWorkflow"),
            ["workspace_path_exists"] = Read("workspace_path_exists", "Проверить путь проекта", "Проверяет существование файла или каталога внутри рабочего репозитория.", "Рабочий проект", ToolCapabilityScope.General, "Существует ли проект DesignerAssistant.Tests?"),
            ["workspace_dotnet_build"] = Read("workspace_dotnet_build", "Собрать .NET-проект", "Запускает dotnet build для выбранного проекта и возвращает фактический код завершения, stdout и stderr.", "Сборка и тесты", ToolCapabilityScope.General, "Собери DesignerAssistant.Web"),
            ["workspace_dotnet_test"] = Read("workspace_dotnet_test", "Запустить .NET-тесты", "Запускает dotnet test для выбранного проекта и возвращает фактический код завершения, stdout и stderr.", "Сборка и тесты", ToolCapabilityScope.General, "Запусти тесты TaskWorkflowTests"),
            ["workspace_run_command_recipe"] = Read("workspace_run_command_recipe", "Запустить разрешённую системную проверку", "Запускает только фиксированные read-only рецепты Git или dotnet; произвольные команды запрещены.", "Среда выполнения", ToolCapabilityScope.General, "Покажи git status"),
            ["workspace_list_processes"] = Read("workspace_list_processes", "Проверить процессы", "Показывает безопасный список процессов без их командных строк.", "Среда выполнения", ToolCapabilityScope.General, "Запущен ли Revit?"),
            ["workspace_is_port_listening"] = Read("workspace_is_port_listening", "Проверить локальный порт", "Проверяет, прослушивается ли указанный локальный TCP-порт.", "Среда выполнения", ToolCapabilityScope.General, "Слушается ли порт 5080?"),
            ["revit_list_available_targets"] = Read("revit_list_available_targets", "Показать доступные сеансы Revit", "Находит запущенные сеансы Revit, к которым может подключиться помощник.", "Подключение", ToolCapabilityScope.General, "Какие сеансы Revit доступны?"),
            ["revit_get_current_target"] = Read("revit_get_current_target", "Показать активный сеанс Revit", "Сообщает, к какому сеансу и документу Revit сейчас подключён помощник.", "Подключение", ToolCapabilityScope.General, "К какому Revit ты подключён?"),
            ["revit_switch_target"] = Read("revit_switch_target", "Переключить сеанс Revit", "Переключает подключение помощника на доступную версию или сеанс Revit; модель не изменяет.", "Подключение", ToolCapabilityScope.General, "Переключись на Revit 2022"),
            ["revit_get_current_view_info"] = Read("revit_get_current_view_info", "Получить сведения об активном виде", "Читает имя, тип и основные свойства текущего активного вида.", "Активный вид", ToolCapabilityScope.View, "Что сейчас открыто на активном виде?"),
            ["revit_get_selected_elements"] = Read("revit_get_selected_elements", "Прочитать выбранные элементы", "Возвращает элементы, которые пользователь выделил в активном документе Revit.", "Элементы", ToolCapabilityScope.Element, "Какие элементы сейчас выделены?"),
            ["revit_get_element_details"] = Read("revit_get_element_details", "Получить сведения об элементе", "Читает категорию, тип, семейство и другие основные сведения по ElementId.", "Элементы", ToolCapabilityScope.Element, "Что за элемент 5133824?"),
            ["revit_get_element_parameters"] = Read("revit_get_element_parameters", "Прочитать параметры элемента", "Возвращает параметры экземпляра с их точными именами; результат нужно использовать для проверки и коррекции parameterName перед операциями с параметрами.", "Параметры", ToolCapabilityScope.Element, "Покажи параметры элемента 5133824"),
            ["revit_get_type_parameters"] = Read("revit_get_type_parameters", "Прочитать параметры типа", "Возвращает параметры типа или семейства для указанного элемента.", "Параметры", ToolCapabilityScope.Element, "Какие параметры типа у элемента 5133824?"),
            ["revit_list_worksets"] = Read("revit_list_worksets", "Показать рабочие наборы", "Перечисляет рабочие наборы открытой модели и их состояние.", "Модель", ToolCapabilityScope.General, "Перечисли рабочие наборы модели"),
            ["revit_analyze_model_statistics"] = Read("revit_analyze_model_statistics", "Проанализировать статистику модели", "Собирает сводную статистику по категориям и элементам открытой модели.", "Модель", ToolCapabilityScope.General, "Покажи статистику модели"),
            ["revit_ai_element_filter"] = Read("revit_ai_element_filter", "Найти элементы по условиям", "Фильтрует элементы модели по категории, параметрам и другим заданным условиям.", "Элементы", ToolCapabilityScope.Element, "Найди стены на текущем уровне"),
            ["revit_custom_summarize_elements"] = Read("revit_custom_summarize_elements", "Показать состав вида", "Считает элементы активного или указанного вида и возвращает компактную сводку по категориям.", "Активный вид", ToolCapabilityScope.View, "Какие элементы есть на активном виде?"),
            ["revit_custom_list_elements"] = Read("revit_custom_list_elements", "Показать элементы категории на виде", "Возвращает небольшую постраничную выборку элементов одной категории из сводки вида.", "Элементы", ToolCapabilityScope.Element, "Покажи первую страницу воздуховодов на активном виде"),
            ["revit_select_elements"] = Ui("revit_select_elements", "Выделить элементы", "Заменяет текущее выделение в интерфейсе Revit указанными ElementId и при необходимости приближает их; модель не изменяет.", "Интерфейс Revit", ToolCapabilityScope.Element, "Выдели элементы 12345 и 67890"),
            ["revit_custom_open_model"] = Write("revit_custom_open_model", "Открыть модель через MCP Bridge", "Передаёт проверенное задание pyRevit-команде MCP Bridge и открывает RVT; поддерживает отсоединение, настройку рабочих наборов и последующую локальную выгрузку связей.", "MCP Bridge", ToolCapabilityScope.General, "Открой C:\\Models\\Project.rvt с отсоединением и закрой все рабочие наборы"),
            ["revit_custom_open_family"] = Write("revit_custom_open_family", "Открыть семейство через MCP Bridge", "Передаёт проверенное задание pyRevit-команде MCP Bridge для открытия RFA, не закрывая другие документы.", "MCP Bridge", ToolCapabilityScope.General, "Открой семейство C:\\Families\\Valve.rfa"),
            ["revit_custom_load_families"] = Write("revit_custom_load_families", "Загрузить семейства в проект", "Загружает один RFA по пути или все RFA верхнего уровня папки в активный проект, обновляя существующие семейства, типы, значения параметров и вложенные общие семейства; подпапки игнорируются.", "Семейства", ToolCapabilityScope.General, "Загрузи все семейства из C:\\Families"),
            ["revit_custom_sync_relinquish_and_close"] = Write("revit_custom_sync_relinquish_and_close", "Синхронизировать, освободить и закрыть", "Синхронизирует активную локальную модель с файлом-хранилищем, освобождает владение и после успешной синхронизации ставит штатную команду закрытия Revit.", "Совместная работа", ToolCapabilityScope.General, "Синхронизируй с освобождением и закрой модель"),
            ["revit_custom_execute_pyrevit_command"] = Write("revit_custom_execute_pyrevit_command", "Нажать pyRevit-кнопку через MCP Bridge", "Находит зарегистрированную Ribbon-кнопку по папке .pushbutton и нажимает её через Revit PostCommand. Выделение необязательно: requiresSelection=true указывается только для команды, которой действительно нужны выбранные элементы.", "MCP Bridge", ToolCapabilityScope.General, "Запусти pyRevit-команду C:\\Extensions\\Tools.tab\\Run.pushbutton без обязательного выделения"),
            ["revit_custom_get_bridge_operation"] = Read("revit_custom_get_bridge_operation", "Проверить операцию MCP Bridge", "По runId возвращает фактическое состояние, результат и обработанные диалоги операции pyRevit.", "MCP Bridge", ToolCapabilityScope.General, "Проверь результат операции открытия"),
            ["revit_custom_list_pyrevit_output_windows"] = Read("revit_custom_list_pyrevit_output_windows", "Прочитать консоли pyRevit", "Возвращает открытые окна вывода pyRevit, их точные идентификаторы и ограниченный текст; по снимку до запуска отмечает новые окна.", "MCP Bridge", ToolCapabilityScope.General, "Проверь, какая консоль pyRevit открылась после запуска"),
            ["revit_custom_close_pyrevit_output_window"] = Ui("revit_custom_close_pyrevit_output_window", "Закрыть консоль pyRevit", "Закрывает только одно окно вывода pyRevit по точному outputUniqueId, не изменяя модель.", "MCP Bridge", ToolCapabilityScope.General, "Закрой консоль pyRevit с указанным outputUniqueId"),
            ["revit_custom_find_pyrevit_buttons"] = Read("revit_custom_find_pyrevit_buttons", "Найти загруженные кнопки pyRevit", "Ищет только реально загруженные в текущем сеансе pyRevit-команды и возвращает видимый текст, commandId, unique name и путь bundle.", "MCP Bridge", ToolCapabilityScope.General, "Найди загруженную кнопку pyRevit Расчёт аэродинамики"),
            ["revit_custom_unload_links_locally"] = Write("revit_custom_unload_links_locally", "Выгрузить связи локально", "Выгружает все загруженные связи Revit только для текущего пользователя в активной локальной модели.", "Связи Revit", ToolCapabilityScope.General, "Выгрузи для меня все связи Revit"),

            ["revit_get_available_family_types"] = Read("revit_get_available_family_types", "Показать доступные типы семейств", "Перечисляет загруженные типы семейств, которые можно использовать при создании элементов.", "Семейства", ToolCapabilityScope.General, "Покажи доступные типы дверей"),
            ["revit_get_material_quantities"] = Read("revit_get_material_quantities", "Рассчитать объёмы материалов", "Возвращает площади и объёмы материалов для указанных элементов.", "Материалы", ToolCapabilityScope.Element, "Покажи объёмы материалов элемента 5133824"),
            ["revit_get_element_relationships"] = Read("revit_get_element_relationships", "Показать связи элемента", "Находит связанные уровни, группы, сборки, помещения и другие отношения указанного элемента.", "Элементы", ToolCapabilityScope.Element, "Покажи связи элемента 5133824"),
            ["revit_list_groups"] = Read("revit_list_groups", "Показать группы модели", "Перечисляет группы модели и группы элементов узлов в проекте.", "Группы и сборки", ToolCapabilityScope.General, "Перечисли группы модели"),
            ["revit_get_group_members"] = Read("revit_get_group_members", "Показать состав группы", "Возвращает элементы, входящие в указанную группу Revit.", "Группы и сборки", ToolCapabilityScope.Element, "Покажи состав группы 2099614"),
            ["revit_list_assemblies"] = Read("revit_list_assemblies", "Показать сборки", "Перечисляет сборки Revit в открытом документе.", "Группы и сборки", ToolCapabilityScope.General, "Перечисли сборки проекта"),
            ["revit_get_assembly_members"] = Read("revit_get_assembly_members", "Показать состав сборки", "Возвращает элементы, входящие в указанную сборку Revit.", "Группы и сборки", ToolCapabilityScope.Element, "Покажи состав сборки 123456"),
            ["revit_list_project_parameters"] = Read("revit_list_project_parameters", "Показать параметры проекта", "Перечисляет параметры проекта, их типы данных и привязанные категории.", "Параметры", ToolCapabilityScope.General, "Перечисли параметры проекта"),

            ["revit_create_line_based_element"] = Write("revit_create_line_based_element", "Создать стену по двум точкам", "Создаёт прямую стену между двумя точками в плане; координаты и высота задаются в миллиметрах.", "Создание элементов", ToolCapabilityScope.Element, "Создай стену от 0,0 до 6000,0 высотой 3000 мм"),
            ["revit_create_point_based_element"] = Write("revit_create_point_based_element", "Разместить экземпляр семейства", "Размещает точечный экземпляр загруженного семейства, например дверь, окно или оборудование, по typeId и координатам.", "Создание элементов", ToolCapabilityScope.Element, "Размести тип 123456 в точке 3000,2000"),
            ["revit_create_surface_based_element"] = Write("revit_create_surface_based_element", "Создать перекрытие или потолок", "Создаёт перекрытие или потолок по замкнутому контуру точек в миллиметрах.", "Создание элементов", ToolCapabilityScope.Element, "Создай перекрытие по прямоугольному контуру"),
            ["revit_create_level"] = Write("revit_create_level", "Создать уровень", "Создаёт уровень на заданной абсолютной отметке в миллиметрах.", "Создание модели", ToolCapabilityScope.General, "Создай уровень +3600 мм с именем Этаж 2"),
            ["revit_create_grid"] = Write("revit_create_grid", "Создать ось", "Создаёт прямую координационную ось между двумя точками в плане.", "Создание модели", ToolCapabilityScope.General, "Создай ось А от 0,0 до 0,12000"),
            ["revit_create_room"] = Write("revit_create_room", "Разместить помещение", "Создаёт помещение в указанной точке уровня и при необходимости задаёт имя и номер.", "Создание модели", ToolCapabilityScope.Element, "Размести помещение 101 на уровне Этаж 1"),
            ["revit_create_group_from_elements"] = Write("revit_create_group_from_elements", "Создать группу из элементов", "Объединяет два или более существующих элемента в новую группу модели.", "Группы и сборки", ToolCapabilityScope.Element, "Создай группу из элементов 12345 и 67890"),

            ["revit_operate_element"] = Write("revit_operate_element", "Выделить или изменить отображение элементов", "Выделяет, скрывает, показывает, изолирует или окрашивает указанные элементы в текущем виде.", "Отображение элементов", ToolCapabilityScope.Element, "Изолируй элементы 12345 и 67890"),
            ["revit_color_elements"] = Write("revit_color_elements", "Окрасить элементы по параметру", "Назначает графические цвета группам элементов в зависимости от значения выбранного параметра.", "Отображение элементов", ToolCapabilityScope.Element, "Окрась воздуховоды по параметру Система"),
            ["revit_set_element_parameter_values"] = Write("revit_set_element_parameter_values", "Изменить параметр экземпляров", "Записывает одно значение параметра в несколько экземпляров элементов; значения длины принимаются в миллиметрах.", "Параметры", ToolCapabilityScope.Element, "Запиши значение Монтаж в Комментарии элементов 12345 и 67890"),
            ["revit_set_type_parameter_values"] = Write("revit_set_type_parameter_values", "Изменить параметр типа", "Записывает значение параметра типа для указанных типов или типов выбранных элементов.", "Параметры", ToolCapabilityScope.Element, "Измени описание типа элемента 12345"),
            ["revit_change_element_type"] = Write("revit_change_element_type", "Сменить тип элементов", "Назначает совместимый ElementType указанным элементам.", "Изменение элементов", ToolCapabilityScope.Element, "Смени тип элементов 12345 и 67890 на тип 24680"),
            ["revit_assign_elements_to_workset"] = Write("revit_assign_elements_to_workset", "Назначить рабочий набор", "Переносит указанные элементы в пользовательский рабочий набор совместного проекта.", "Рабочие наборы", ToolCapabilityScope.Element, "Назначь элементы 12345 и 67890 рабочему набору ОВ"),
            ["revit_delete_element"] = Write("revit_delete_element", "Удалить элементы", "Удаляет элементы по ElementId вместе с зависимостями, которые удаляет Revit; операция требует особенно внимательной проверки.", "Удаление", ToolCapabilityScope.Element, "Удали элемент 12345"),

            ["revit_create_view"] = Write("revit_create_view", "Создать вид", "Создаёт план этажа или 3D-вид и при необходимости задаёт его имя.", "Виды", ToolCapabilityScope.View, "Создай план уровня Этаж 1"),
            ["revit_place_view_on_sheet"] = Write("revit_place_view_on_sheet", "Разместить вид на листе", "Размещает указанный вид на существующем листе либо создаёт новый лист для размещения.", "Листы и виды", ToolCapabilityScope.View, "Размести вид 12345 на листе 67890"),
            ["revit_analyze_sheet_layout"] = Read("revit_analyze_sheet_layout", "Проанализировать компоновку листа", "Читает границы основной надписи и размещённых видовых экранов в миллиметрах.", "Листы и виды", ToolCapabilityScope.View, "Проанализируй компоновку текущего листа"),
            ["revit_capture_view_image"] = Read("revit_capture_view_image", "Сохранить изображение вида", "Экспортирует активный или указанный вид в PNG или JPEG внутри разрешённой локальной папки.", "Виды", ToolCapabilityScope.View, "Сохрани изображение активного вида"),
            ["revit_set_view_crop"] = Write("revit_set_view_crop", "Настроить область обрезки вида", "Включает и изменяет область обрезки вида по границам или выбранным элементам.", "Виды", ToolCapabilityScope.View, "Подгони обрезку активного вида по элементам 12345 и 67890"),
            ["revit_set_view_scale"] = Write("revit_set_view_scale", "Изменить масштаб вида", "Устанавливает знаменатель графического масштаба вида, например 50 для масштаба 1:50.", "Виды", ToolCapabilityScope.View, "Установи масштаб активного вида 1:50"),
            ["revit_activate_view"] = Ui("revit_activate_view", "Открыть вид", "Делает указанный вид активным в интерфейсе Revit, не изменяя модель.", "Виды", ToolCapabilityScope.View, "Открой вид Координация"),
            ["revit_show_element_in_view"] = Ui("revit_show_element_in_view", "Показать элементы на виде", "Открывает подходящий вид, выделяет указанные элементы и приближает их в интерфейсе Revit.", "Виды", ToolCapabilityScope.Element, "Покажи элемент 5133824 на виде"),

            ["revit_analyze_usage_patterns"] = Read("revit_analyze_usage_patterns", "Показать статистику использования инструментов", "Анализирует локальную историю вызовов MCP-инструментов без изменения модели.", "Служебные операции", ToolCapabilityScope.General, "Покажи статистику использования инструментов"),
            ["revit_batch_execute"] = Write("revit_batch_execute", "Выполнить пакет команд", "Последовательно выполняет несколько MCP-команд как один управляемый пакет с общей проверкой результатов.", "Служебные операции", ToolCapabilityScope.General, "Выполни подготовленный пакет команд"),
            ["revit_purge_unused"] = Write("revit_purge_unused", "Найти или удалить неиспользуемые типы", "В безопасном режиме показывает, а по явной команде удаляет неиспользуемые типы загружаемых семейств.", "Очистка модели", ToolCapabilityScope.General, "Покажи, что можно очистить, без удаления"),
            ["revit_send_code_to_revit"] = Write("revit_send_code_to_revit", "Выполнить C# в Revit", "Компилирует и выполняет произвольный C# внутри Revit; применяется только когда нет подходящего типизированного инструмента.", "Расширенные операции", ToolCapabilityScope.General, "Выполни согласованный C#-скрипт в Revit"),
            ["revit_set_project_info"] = Write("revit_set_project_info", "Изменить сведения о проекте", "Записывает название, номер, клиента, адрес, статус или дату выпуска в сведения о проекте.", "Проект", ToolCapabilityScope.General, "Укажи номер проекта 2026-01"),
            ["revit_show_message"] = Ui("revit_show_message", "Показать сообщение в Revit", "Открывает информационное окно TaskDialog в интерфейсе Revit.", "Интерфейс Revit", ToolCapabilityScope.General, "Покажи в Revit сообщение Проверка завершена")
        };

    public static bool TryDetectQuestion(string message, out ToolCapabilityScope scope)
    {
        scope = ToolCapabilityScope.General;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = Regex.Replace(message.ToLowerInvariant(), @"\s+", " ");
        var hasCapabilityNoun = Regex.IsMatch(
            text,
            @"\b(функционал\w*|возможност\w*|инструмент\w*|команд\w*|операци\w*|действи(?:е|я|й|ем|ю|ям|ями|ях))\b");
        var hasInquiryMarker = Regex.IsMatch(
            text,
            @"\b(что|какие|какой|чем|перечисли|покажи|опиши|расскажи|названи\w*|доступн\w*|есть|имеются|поддержива\w*)\b");
        var asksAboutCapability =
            hasCapabilityNoun && hasInquiryMarker ||
            Regex.IsMatch(text, @"\b(что|какие|какой|чем)\b.{0,50}\b(умеешь|можешь|можно|доступн\w*|поддерживаешь|способен)\b") ||
            Regex.IsMatch(text, @"\b(перечисли|покажи|опиши|расскажи)\b.{0,50}\b(функционал\w*|возможност\w*|инструмент\w*|команд\w*|операци\w*|действи(?:е|я|й|ем|ю|ям|ями|ях))\b") ||
            Regex.IsMatch(text, @"\b(функционал\w*|возможност\w*|инструмент\w*)\b.{0,35}\b(есть|доступн\w*|имеются|поддержива\w*)\b") ||
            Regex.IsMatch(text, @"\bчто\s+ты\s+умеешь\b|\bтвои\s+возможности\b|\bсписок\s+инструмент\w*\b");
        if (!asksAboutCapability) return false;

        if (Regex.IsMatch(text, @"\b(активн\w*|текущ\w*|открыт\w*|эт\w*)\s+вид\w*\b|\b(для|с|над)\s+вид\w*\b|\bвид\w*\s+revit\b"))
            scope = ToolCapabilityScope.View;
        else if (Regex.IsMatch(text, @"\b(элемент\w*|объект\w*|выделен\w*|параметр\w*)\b")) scope = ToolCapabilityScope.Element;
        return true;
    }

    public static IReadOnlyList<ToolCapability> Describe(IReadOnlyCollection<ToolDefinition> tools) =>
        tools.Select(Describe).ToArray();

    public static bool IsWriteTool(ToolDefinition tool) => Describe(tool).IsWrite;

    public static ToolCapability Get(ToolDefinition tool) => Describe(tool);

    public static bool RequiresConfirmation(ToolDefinition tool) =>
        Describe(tool).IsWrite || tool.Name is
            "revit_select_elements" or
            "revit_custom_execute_pyrevit_command" or
            "revit_custom_close_pyrevit_output_window" or
            "revit_activate_view" or
            "revit_show_element_in_view" or
            "revit_show_message";

    public static string BuildUserSummary(
        IReadOnlyCollection<ToolDefinition> tools,
        ToolCapabilityScope requestedScope)
    {
        var all = Describe(tools);
        var selected = requestedScope == ToolCapabilityScope.General
            ? all
            : all.Where(item => item.Scope == requestedScope || IsRelevantWrite(item, requestedScope)).ToArray();

        var heading = requestedScope switch
        {
            ToolCapabilityScope.View => "С активным видом через доступные инструменты я могу:",
            ToolCapabilityScope.Element => "С элементами через доступные инструменты я могу:",
            _ => "Сейчас мне доступны такие возможности:"
        };
        if (selected.Count == 0)
        {
            return $"{heading}\n\nВ текущем каталоге нет инструментов для этой области. Я не буду придумывать недоступные операции.";
        }

        var builder = new StringBuilder(heading);
        foreach (var group in selected.GroupBy(item => item.Category))
        {
            builder.AppendLine().AppendLine().Append(group.Key).Append(':');
            foreach (var item in group)
            {
                builder.AppendLine()
                    .Append("- ").Append(item.Title).Append(" — ").Append(item.Description);
                if (item.IsWrite) builder.Append(" Требует отдельного подтверждения перед изменением модели.");
                builder.Append(" Например: «").Append(item.Example).Append("».");
            }
        }
        builder.AppendLine().AppendLine()
            .Append(BuildBridgeHelp(tools)).AppendLine().AppendLine()
            .Append("Перечень сформирован по текущему каталогу инструментов. Операции без изменения модели выполняются сразу; изменяющие модель Revit — только по явной команде и после подтверждения.");
        return builder.ToString();
    }

    public static bool IsBridgeQuestion(string message) =>
        !string.IsNullOrWhiteSpace(message) &&
        Regex.IsMatch(message, @"\b(mcp\s*bridge|pyrevit|пиревит)\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(message, @"\b(что|какие|как|дай|дать|нужн\w*|требу\w*)\b", RegexOptions.IgnoreCase);

    public static string BuildBridgeHelp(IReadOnlyCollection<ToolDefinition> tools)
    {
        var available = tools.Select(tool => tool.Name).Where(name => name.StartsWith("revit_custom_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var supported = new List<string>();
        if (available.Contains("revit_custom_open_model")) supported.Add("открытие RVT с настройками отсоединения и рабочих наборов");
        if (available.Contains("revit_custom_open_family")) supported.Add("открытие RFA");
        if (available.Contains("revit_custom_load_families")) supported.Add("загрузка и обновление семейств из файла или папки");
        if (available.Contains("revit_custom_sync_relinquish_and_close")) supported.Add("синхронизация с освобождением и закрытием модели");
        if (available.Contains("revit_custom_execute_pyrevit_command")) supported.Add("запуск команды из папки .pushbutton с необязательным выделением");
        if (available.Contains("revit_custom_list_pyrevit_output_windows")) supported.Add("чтение новых окон вывода pyRevit после запуска");
        if (available.Contains("revit_custom_close_pyrevit_output_window")) supported.Add("закрытие конкретного окна вывода по ID");
        if (available.Contains("revit_custom_find_pyrevit_buttons")) supported.Add("поиск реально загруженных Ribbon-кнопок и их commandId");
        if (available.Contains("revit_custom_unload_links_locally")) supported.Add("локальная выгрузка Revit-связей");
        var supportedText = supported.Count == 0 ? "готовые операции сейчас не обнаружены" : string.Join(", ", supported);
        return $"MCP Bridge связывает MCP-инструменты с pyRevit. Сейчас поддерживаются: {supportedText}. Для запуска передайте путь к папке .pushbutton. Выделение не требуется по умолчанию; requiresSelection=true нужно указывать только для скрипта, которому действительно нужны выбранные элементы. При необходимости заранее подготовьте активный вид. Команда выполняется только после подтверждения; после нажатия кнопки новые консоли определяются относительно снимка outputWindowIdsBefore.";
    }

    public static string BuildCompactRouterCatalogue(IReadOnlyCollection<ToolDefinition> tools) =>
        string.Join(Environment.NewLine, Describe(tools).Select(item =>
            $"- {item.ToolName} | {(item.IsWrite ? "write" : "read")} | {item.Title}: {item.Description}"));

    private static ToolCapability Describe(ToolDefinition tool)
    {
        if (Known.TryGetValue(tool.Name, out var known)) return known;

        var isClearlyReadOnly = Regex.IsMatch(
            tool.Name,
            @"^revit_(get|list|analyze)_",
            RegexOptions.IgnoreCase);
        var isWrite = !isClearlyReadOnly;
        var scope = Regex.IsMatch(tool.Name, "view", RegexOptions.IgnoreCase)
            ? ToolCapabilityScope.View
            : Regex.IsMatch(tool.Name, "element|parameter|family|type|selection", RegexOptions.IgnoreCase)
                ? ToolCapabilityScope.Element
                : ToolCapabilityScope.General;
        var title = $"Инструмент {tool.Name}";
        var description = string.IsNullOrWhiteSpace(tool.Description)
            ? "Для этого нового инструмента ещё не подготовлено проверенное русское описание"
            : tool.Description.Trim().TrimEnd('.');
        return new ToolCapability(
            tool.Name,
            title,
            description + ".",
            isWrite ? "Изменение модели" : CategoryFor(scope),
            scope,
            isWrite,
            isWrite ? $"Вызови {tool.Name} после подтверждения" : $"Вызови {tool.Name}");
    }

    private static ToolCapability Read(string name, string title, string description, string category, ToolCapabilityScope scope, string example) =>
        new(name, title, description, category, scope, false, example);

    private static ToolCapability Ui(string name, string title, string description, string category, ToolCapabilityScope scope, string example) =>
        new(name, title, description, category, scope, false, example);

    private static ToolCapability Write(string name, string title, string description, string category, ToolCapabilityScope scope, string example) =>
        new(name, title, description, category, scope, true, example);

    private static bool IsRelevantWrite(ToolCapability item, ToolCapabilityScope scope) =>
        item.IsWrite && (item.Scope == scope || item.Scope == ToolCapabilityScope.General);

    private static string CategoryFor(ToolCapabilityScope scope) => scope switch
    {
        ToolCapabilityScope.View => "Активный вид",
        ToolCapabilityScope.Element => "Элементы",
        _ => "Модель"
    };

}
