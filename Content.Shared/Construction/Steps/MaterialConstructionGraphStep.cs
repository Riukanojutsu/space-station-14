using System.Diagnostics.CodeAnalysis;
using Content.Shared.Examine;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;
using Robust.Shared.Utility.Markup;

namespace Content.Shared.Construction.Steps
{
    [DataDefinition]
    public class MaterialConstructionGraphStep : EntityInsertConstructionGraphStep
    {
        // TODO: Make this use the material system.
        // TODO TODO: Make the material system not shit.
        [DataField("material", required:true, customTypeSerializer:typeof(PrototypeIdSerializer<StackPrototype>))]
        public string MaterialPrototypeId { get; } = "Steel";

        [DataField("amount")] public int Amount { get; } = 1;

        public override void DoExamine(ExaminedEvent examinedEvent)
        {
            var material = IoCManager.Resolve<IPrototypeManager>().Index<StackPrototype>(MaterialPrototypeId);

            examinedEvent.Message.AddMessage(Basic.RenderMarkup(Loc.GetString("construction-insert-material-entity", ("amount", Amount), ("materialName", material.Name))));
        }

        public override bool EntityValid(EntityUid uid, IEntityManager entityManager)
        {
            return entityManager.TryGetComponent(uid, out SharedStackComponent? stack) && stack.StackTypeId.Equals(MaterialPrototypeId) && stack.Count >= Amount;
        }

        public bool EntityValid(EntityUid entity, [NotNullWhen(true)] out SharedStackComponent? stack)
        {
            if (IoCManager.Resolve<IEntityManager>().TryGetComponent(entity, out SharedStackComponent? otherStack) && otherStack.StackTypeId.Equals(MaterialPrototypeId) && otherStack.Count >= Amount)
                stack = otherStack;
            else
                stack = null;

            return stack != null;
        }

        public override ConstructionGuideEntry GenerateGuideEntry()
        {
            var material = IoCManager.Resolve<IPrototypeManager>().Index<StackPrototype>(MaterialPrototypeId);

            return new ConstructionGuideEntry()
            {
                Localization = "construction-presenter-material-step",
                Arguments = new (string, object)[]{("amount", Amount), ("material", material.Name)},
                Icon = material.Icon,
            };
        }
    }
}
