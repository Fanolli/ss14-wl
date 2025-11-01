using Content.Shared.Actions.Components;
using Content.Shared.Body.Components;
using Content.Shared.Storage;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Robust.Server.Containers;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Generic;
using Robust.Shared.Serialization.TypeSerializers.Interfaces;
using Robust.Shared.Utility;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Content.Server._WL.GameObjects.Systems;

/// <summary>
/// Система для глубокого копирования сущностей.
/// Копируются только <see cref="DataFieldAttribute"/>-члены компонентов.
/// </summary>
public sealed partial class EntityCopySystem : EntitySystem
{
    // TODO: копирование runtime-членов?....

    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly ILogManager _logManager = default!;
    [Dependency] private readonly ContainerSystem _container = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly ISerializationManager _serialization = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private static readonly ConcurrentDictionary<Type, object> _cacheCreators = new();

    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = _logManager.GetSawmill("entity.copy");
    }

    #region Public api
    /// <summary>
    /// Копирует сущность в указанные <see cref="MapCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="container">Контейнер, в который будет помещена сущность.</param>
    /// <param name="coordinates">Координаты для спавна копии.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    public EntityUid? CopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        MapCoordinates coordinates,
        BaseContainer? container = null,
        Angle rotation = default,
        bool initialize = true
        )
    {
        TryCopyEntity(sourceEntity, coordinates, out var copy, container, rotation, initialize);
        return copy;
    }

    /// <summary>
    /// Копирует сущность в указанные <see cref="EntityCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="container">Контейнер, в который будет помещена сущность.</param>
    /// <param name="coordinates">Координаты для спавна копии.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    public EntityUid? CopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        EntityCoordinates coordinates,
        BaseContainer? container = null,
        Angle rotation = default,
        bool initialize = true
        )
    {
        TryCopyEntity(sourceEntity, coordinates, out var copy, container, rotation, initialize);
        return copy;
    }

    /// <summary>
    /// Копирует сущность без привязки к карте (<see cref="MapCoordinates.Nullspace"/>).
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="container">Контейнер, в который будет помещена сущность.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    public EntityUid? CopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        BaseContainer? container = null,
        Angle rotation = default,
        bool initialize = false
        )
    {
        TryCopyEntity(sourceEntity, MapCoordinates.Nullspace, out var copy, container, rotation, initialize);
        return copy;
    }

    /// <summary>
    /// Пытается скопировать сущность в указанные <see cref="MapCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="mapCoordinates">Координаты для спавна копии.</param>
    /// <param name="copiedEntity"><see cref="EntityUid"/> скопированной сущности.</param>
    /// <param name="containerToStore">Контейнер, в который будет помещена сущность.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    /// <returns>
    /// <see langword="true"/> - если копирование прошло успешно, <see langword="false"/> - если нет.
    /// </returns>
    public bool TryCopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        MapCoordinates mapCoordinates,
        [NotNullWhen(true)] out EntityUid? copiedEntity,
        BaseContainer? containerToStore = null,
        Angle rotation = default,
        bool initialize = true
        )
    {
        return TryCopyEntityInternal(sourceEntity, (proto) =>
        {
            return EntityManager.CreateEntityUninitialized(proto.ID, mapCoordinates, null, rotation);
        }, out copiedEntity, initialize, containerToStore);
    }

    /// <summary>
    /// Пытается скопировать сущность в указанные <see cref="EntityCoordinates"/>.
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="entCoordinates">Координаты для спавна копии.</param>
    /// <param name="copiedEntity"><see cref="EntityUid"/> скопированной сущности.</param>
    /// <param name="containerToStore">Контейнер, в который будет помещена сущность.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    /// <returns>
    /// <see langword="true"/> - если копирование прошло успешно, <see langword="false"/> - если нет.
    /// </returns>
    public bool TryCopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        EntityCoordinates entCoordinates,
        [NotNullWhen(true)] out EntityUid? copiedEntity,
        BaseContainer? containerToStore = null,
        Angle rotation = default,
        bool initialize = true
        )
    {
        return TryCopyEntityInternal(sourceEntity, (proto) =>
        {
            return EntityManager.CreateEntityUninitialized(proto.ID, entCoordinates, null, rotation);
        }, out copiedEntity, initialize, containerToStore);
    }

    /// <summary>
    /// Пытается скопировать сущность без привязки к карте (<see cref="MapCoordinates.Nullspace"/>).
    /// </summary>
    /// <param name="sourceEntity">Цель для копирования.</param>
    /// <param name="copiedEntity"><see cref="EntityUid"/> скопированной сущности.</param>
    /// <param name="containerToStore">Контейнер, в который будет помещена сущность.</param>
    /// <param name="rotation">Угол поворота копии.</param>
    /// <param name="initialize">Нужно ли инициализировать скопированную сущность.</param>
    /// <returns>
    /// <see langword="true"/> - если копирование прошло успешно, <see langword="false"/> - если нет.
    /// </returns>
    public bool TryCopyEntity(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        [NotNullWhen(true)] out EntityUid? copiedEntity,
        BaseContainer? containerToStore = null,
        Angle rotation = default,
        bool initialize = true
        )
    {
        return TryCopyEntityInternal(sourceEntity, (proto) =>
        {
            return EntityManager.CreateEntityUninitialized(proto.ID, MapCoordinates.Nullspace, null, rotation);
        }, out copiedEntity, initialize, containerToStore);
    }

    /// <summary>
    /// Может ли сущность быть скопирована?
    /// </summary>
    public bool CanCopyEntity(EntityUid entity)
    {
        // TODO: нужно протестировать и возможно потом дополнить логикой какой-нибудь.
        return true;
    }
    #endregion

    #region Private stuff
    private Dictionary<Type, Component> GetComps(EntityUid ent)
    {
        var comps = AllComps(ent)
            .Where(c => c is not MetaDataComponent && c is not TransformComponent) /// строка 590 <see cref="EntityManager.RemoveComponentDeferred(EntityUid, Component)"/>
            .Where(c => c is not ActorComponent) // на всякий случай :despair:
            .Where(c => c is not ActionsContainerComponent)
            .Where(c => c is not FixturesComponent)
            .Where(c => c is not BodyComponent)
            .Where(c => c is not StorageComponent)
            .Select(c => (Component)c)
            .ToDictionary(k => k.GetType(), v => v);

        return comps;
    }

    private void EnsureDataFields(Component origin, Component copy, out List<EntityUid>? toInit)
    {
        toInit = null;

        const BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.IgnoreCase |
            BindingFlags.Public |
            BindingFlags.NonPublic;

        if (TryHandleAhhhComponents(origin, copy, out toInit))
            return;

        var type = origin.GetType();

        foreach (var field in type.GetFields(flags))
        {
            var datafield = field.GetCustomAttribute<DataFieldAttribute>();
            if (datafield == null)
                continue;

            var customSerializer = datafield.CustomTypeSerializer;

            var originValue = field.GetValue(origin);
            var copyValue = field.GetValue(copy);

            if (Equals(originValue, copyValue))
                continue;

            var copiedValue = CreateCopySafe(origin, field, originValue, customSerializer);

            field.SetValue(copy, copiedValue);
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (!prop.CanWrite)
                continue;

            var datafield = prop.GetCustomAttribute<DataFieldAttribute>();
            if (datafield == null)
                continue;

            var customSerializer = datafield.CustomTypeSerializer;

            var originValue = prop.GetValue(origin);
            var copyValue = prop.GetValue(copy);

            if (Equals(originValue, copyValue))
                continue;

            var copiedValue = CreateCopySafe(origin, prop, originValue, customSerializer);

            prop.SetValue(copy, copiedValue);
        }
    }

    private object? CreateCopySafe(Component originComp, MemberInfo member, object? originValue, Type? customTypeSerializer)
    {
        if (customTypeSerializer == null)
            return _serialization.CreateCopy(originValue);

        var creatorInstance = _cacheCreators.GetOrAdd(customTypeSerializer, (type) =>
        {
            return Activator.CreateInstance(customTypeSerializer)
                ?? throw new NotImplementedException($"Failed to create instance of custom serializer {customTypeSerializer} for {originComp.GetType()}.{member.Name}");
        });

        var creatorInstanceMethod = customTypeSerializer.GetMethod(nameof(ITypeCopyCreator<object>.CreateCopy));

        return creatorInstanceMethod?.Invoke(creatorInstance,
        [
            _serialization,
            originValue,
            ((SerializationManager)_serialization).DependencyCollection,
            SerializationHookContext.ForSkipHooks(false)
        ]);
    }

    /// TODO: заменить свойства на <see cref="Entity{T}"/>
    private bool TryHandleAhhhComponents(Component origin, Component copy, [NotNullWhen(true)] out List<EntityUid>? toInit)
    {
        toInit = null;

        if (TryCastAhhhComponents<ContainerManagerComponent>(out var castOrigin, out var castCopy))
        {
            toInit = [];

            var originEnt = origin.Owner;
            var copyEnt = copy.Owner;

            foreach (var (id, baseContainer) in castOrigin.Containers)
            {
                if (!castCopy.Containers.TryGetValue(id, out var copyContainer))
                {
                    if (baseContainer is Container)
                        copyContainer = _container.EnsureContainer<Container>(copyEnt, id, castCopy);
                    else if (baseContainer is ContainerSlot)
                        copyContainer = _container.EnsureContainer<ContainerSlot>(copyEnt, id, castCopy);
                    else
                        throw new NotSupportedException($"Unsupported container type - {baseContainer.GetType().Name}");
                }

                var exceptions = new List<Exception>(8);
                foreach (var entity in baseContainer.ContainedEntities)
                {
                    if (!TryCopyEntity(entity, out var childCopy, copyContainer, rotation: default, initialize: false))
                        exceptions.Add(new Exception($"Failed to copy entity {ToPrettyString(entity)} from container {id}"));
                    else toInit.Add(childCopy.Value);
                }

                if (exceptions.Count > 0)
                {
                    var agg = new AggregateException(
                        $"Failed to full copy {baseContainer.GetType().Name} container for entity {ToPrettyString(originEnt)}.",
                        exceptions);

                    _sawmill.Error(agg.ToStringBetter());
                }
            }

            return true;
        }

        return false;

        bool TryCastAhhhComponents<T>(
            [NotNullWhen(true)] out T? origin_,
            [NotNullWhen(true)] out T? copy_
            )
            where T : notnull, Component
        {
            origin_ = default;
            copy_ = default;

            if (origin is T originT && copy is T copyT)
            {
                origin_ = originT;
                copy_ = copyT;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Вся основная логика тут.
    /// Нужен только потому, что <see cref="EntityManager.CreateEntityUninitialized(string?, EntityCoordinates, ComponentRegistry?, Angle)"/>
    /// и <see cref="EntityManager.CreateEntityUninitialized(string?, MapCoordinates, ComponentRegistry?, Angle)"/>
    /// имеют разную логику.
    /// </summary>
    private bool TryCopyEntityInternal(
        Entity<MetaDataComponent?, TransformComponent?> sourceEntity,
        Func<EntityPrototype, EntityUid> getCopyFunc,
        [NotNullWhen(true)] out EntityUid? copiedEntity,
        bool initialize = true,
        BaseContainer? container = null
        )
    {
        copiedEntity = null;

        var (origin, originMeta, originXform) = sourceEntity;

        if (!Resolve(origin, ref originMeta, ref originXform, false))
            return false;

        var originProto = originMeta.EntityPrototype;
        if (originProto == null)
            return false;

        var copy = getCopyFunc(originProto);

        // компонент stuff
        var originComps = GetComps(origin);

        var copyComps = GetComps(copy);

        foreach (var (originCompType, _) in originComps)
        {
            if (!copyComps.ContainsKey(originCompType))
            {
                var compUnchecked = _componentFactory.GetComponent(originCompType);
                AddComp(copy, compUnchecked, overwrite: false);
            }
        }

        var childrensToInit = new List<EntityUid>();

        foreach (var (copyCompType, copyCompInst) in copyComps)
        {
            if (!originComps.TryGetValue(copyCompType, out var originCompInst))
            {
                RemCompDeferred(copy, copyCompInst);
                continue;
            }

            EnsureDataFields(originCompInst, copyCompInst, out var toInit);

            if (toInit != null)
                childrensToInit.AddRange(toInit);
        }

        // метадата
        var copyMeta = MetaData(copy);

        _metaData.SetEntityDescription(copy, originMeta.EntityDescription, copyMeta);
        _metaData.SetEntityName(copy, originMeta.EntityName, copyMeta);
        //_metaData.SetFlag((copy, copyMeta), originMeta.Flags, enabled: true);

        // (㇏(•̀ᢍ•́)ノ)
        var copyXform = Transform(copy);

        if (originXform.Anchored && !copyXform.Anchored)
            _transform.AnchorEntity((copy, copyXform));
        else if (!originXform.Anchored && copyXform.Anchored)
            _transform.Unanchor(copy, copyXform);

        // контейнеры
        if (container != null)
            _container.Insert(copy, container, force: true);

        // иницализация
        if (initialize)
        {
            EntityManager.InitializeAndStartEntity(copy, doMapInit: true);

            foreach (var children in childrensToInit)
            {
                EntityManager.InitializeAndStartEntity(children, doMapInit: true);
            }
        }

        copiedEntity = copy;
        return true;
    }
    #endregion
}
