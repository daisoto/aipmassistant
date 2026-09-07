namespace Pm.Domain;

/// <summary>Сторона ответственности. Кейс требует различать команду, клиента и подрядчика.</summary>
public enum Side
{
    Xpage,
    Client,
    Contractor
}

/// <summary>Тип сущности трекера.</summary>
public enum ItemKind
{
    Task,
    Decision,
    Requirement,
    OpenQuestion,
    Dependency
}

public enum ItemStatus
{
    Open,
    Done,
    Cancelled,
    Superseded
}

public enum SourceKind
{
    ClientChat,
    InternalChat,
    Email,
    CallTranscript
}

/// <summary>
/// Как срок попал в задачу.
/// None  — в коммуникации срока нет.
/// Explicit — есть дословная формулировка, которую удалось разобрать в дату.
/// Vague — срок упомянут, но неконкретно («срок не критичный», «в следующем спринте»).
///          Такая задача обязана попасть в секцию «Требует уточнения».
/// </summary>
public enum DueKind
{
    None,
    Explicit,
    Vague
}

/// <summary>Решение резолвера: что сделать с кандидатом относительно уже известного состояния.</summary>
public enum ResolveOp
{
    New,
    Update,
    Supersede,
    Close,
    Noise
}

public enum EvidenceRole
{
    Origin,
    Confirmation,
    Update
}

/// <summary>Пять срезов трекера, которых требует кейс.</summary>
public enum TrackerView
{
    DoMyself,
    DelegateToTeam,
    AskClient,
    WatchContractor,
    GetBackToClient
}
