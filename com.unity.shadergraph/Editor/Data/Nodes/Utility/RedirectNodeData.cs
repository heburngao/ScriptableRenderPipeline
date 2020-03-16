using System.Collections.Generic;
using System.Linq;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;
using Edge = UnityEditor.Experimental.GraphView.Edge;

namespace UnityEditor.ShaderGraph
{
    class RedirectNodeData : AbstractMaterialNode
    {
        public Edge m_Edge;

        // Maybe think of this in reverse?
        SlotReference m_slotReferenceInput;
        public SlotReference slotReferenceInput
        {
            get => m_slotReferenceInput;
            set => m_slotReferenceInput = value;
        }

        SlotReference m_slotReferenceOutput;
        public SlotReference slotReferenceOutput
        {
            get => m_slotReferenceOutput;
            set => m_slotReferenceOutput = value;
        }

        public RedirectNodeData()
        {
            name = "Redirect Node";
        }

        // Center the node's position?
        public void SetPosition(Vector2 pos)
        {
            var temp = drawState;
            temp.position = new Rect(pos, Vector2.zero);
            drawState = temp;
        }

        public override void ValidateNode()
        {
            base.ValidateNode();

            bool noInputs = false;
            bool noOutputs = false;
            var slots = new List<ISlot>();
            GetInputSlots(slots);

            foreach (var inSlot in slots)
            {
                var edges = owner.GetEdges(inSlot.slotReference).ToList();
                noInputs = !edges.Any();

            }
            slots.Clear();
            GetOutputSlots(slots);
            foreach (var outSlot in slots)
            {
                var edges = owner.GetEdges(outSlot.slotReference).ToList();
                noOutputs = !edges.Any();
            }

            if(noInputs && !noOutputs)
            {
                owner.AddValidationError(tempId, "There seems to be Muppets in the choir here!");
            }
        }
    }
}
