using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

using UnityEditor.Graphing;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.Experimental.GraphView;
using UnityEditor.Rendering;
using Edge = UnityEditor.Experimental.GraphView.Edge;

namespace UnityEditor.ShaderGraph
{
    class RedirectNodeView : RedirectNode, IShaderNodeView
    {
        IEdgeConnectorListener m_ConnectorListener;
        //VisualElement m_TitleContainer;
        GraphView m_GraphView;

        public RedirectNodeView() : base()
        {
        }

        public override void InitializeFromEdge(Edge edge, GraphView graphView)
        {
            // Created from de-serialization
            if(edge == null)
                return;

            orientation = edge.output.orientation;
            SplitEdge(edge, graphView);
        }

        // Tie the nodeView to its data
        public void ConnectToData(AbstractMaterialNode inNode, IEdgeConnectorListener connectorListener, GraphView graphView)
        {
            if (inNode == null)
                return;

            // Set references
            var nodeData = inNode as RedirectNodeData;
            node = inNode;
            title = "";
            m_GraphView = graphView;
            m_ConnectorListener = connectorListener;
            //m_TitleContainer = this.Q("title");

            viewDataKey = node.guid.ToString();

            // Set the VisualElement's position
            SetPosition(new Rect(node.drawState.position.x, node.drawState.position.y, 0, 0));
            AddSlots(node.GetSlots<MaterialSlot>());

            InitializeFromEdge(nodeData.m_Edge, m_GraphView);
        }

        public void AddSlots(IEnumerable<MaterialSlot> slots)
        {
            foreach (var slot in slots)
            {
                if (slot.hidden)
                    continue;

                var port = ShaderPort.Create(slot, m_ConnectorListener);

                if (slot.isOutputSlot)
                    outputContainer.Add(port);
                else
                    inputContainer.Add(port);
            }
        }

        public override void SplitEdge(Edge edge, GraphView graphView)
        {
            var nodeData = userData as AbstractMaterialNode;
            var matGraphView = graphView as MaterialGraphView;

            if (edge != null)
            {
                var edge_outSlot = edge.output.GetSlot();
                var edge_inSlot = edge.input.GetSlot();

                var edge_outSlotRef = edge_outSlot.owner.GetSlotReference(edge_outSlot.id);
                var edge_inSlotRef = edge_inSlot.owner.GetSlotReference(edge_inSlot.id);

                // Hard-coded for single input-output. Changes would be needed for multi-input redirects
                var node_inSlotRef = nodeData.GetSlotReference(0);
                var node_outSlotRef = nodeData.GetSlotReference(1);

                matGraphView.graph.Connect(edge_outSlotRef, node_inSlotRef);
                matGraphView.graph.Connect(node_outSlotRef, edge_inSlotRef);
            }

            // Set the color of the ports
            // MTT Remove this
            // foreach (var port in inputContainer.Query<Port>().ToList())
            // {
            //     port.visualClass = edge.output.GetSlot().concreteValueType.ToClassName();
            // }
            //
            // foreach (var port in outputContainer.Query<Port>().ToList())
            // {
            //     port.visualClass = edge.output.GetSlot().concreteValueType.ToClassName();
            // }
        }

        #region IShaderNodeView interface
        public Node gvNode => this;
        public AbstractMaterialNode node { get; private set; }
        public VisualElement colorElement { get { return this; } }

        public void Dispose()
        {
            node = null;
            userData = null;
        }

        public void UpdatePortInputTypes()
        {
            foreach (var anchor in inputContainer.Children().Concat(outputContainer.Children()).OfType<ShaderPort>())
            {
                var slot = anchor.slot;
                anchor.portName = slot.displayName;
                anchor.visualClass = slot.concreteValueType.ToClassName();
            }
        }

        public void OnModified(ModificationScope scope)
        {
            if (scope == ModificationScope.Topological)
            {
                var slots = node.GetSlots<MaterialSlot>().ToList();

                var inputPorts = inputContainer.Children().OfType<ShaderPort>().ToList();
                foreach (var port in inputPorts)
                {
                    var currentSlot = port.slot;
                    var newSlot = slots.FirstOrDefault(s => s.id == currentSlot.id);
                    if (newSlot == null)
                    {
                        // Slot doesn't exist anymore, remove it
                        inputContainer.Remove(port);
                    }
                    else
                    {
                        port.slot = newSlot;
                        slots.Remove(newSlot);
                    }
                }

                var outputPorts = outputContainer.Children().OfType<ShaderPort>().ToList();
                foreach (var port in outputPorts)
                {
                    var currentSlot = port.slot;
                    var newSlot = slots.FirstOrDefault(s => s.id == currentSlot.id);
                    if (newSlot == null)
                    {
                        outputContainer.Remove(port);
                    }
                    else
                    {
                        port.slot = newSlot;
                        slots.Remove(newSlot);
                    }
                }

                AddSlots(slots);

                slots.Clear();
                slots.AddRange(node.GetSlots<MaterialSlot>());

                if (inputContainer.childCount > 0)
                    inputContainer.Sort((x, y) => slots.IndexOf(((ShaderPort)x).slot) - slots.IndexOf(((ShaderPort)y).slot));
                if (outputContainer.childCount > 0)
                    outputContainer.Sort((x, y) => slots.IndexOf(((ShaderPort)x).slot) - slots.IndexOf(((ShaderPort)y).slot));
            }
        }

        public void AttachMessage(string errString, ShaderCompilerMessageSeverity severity)
        {
            ClearMessage();
            IconBadge badge;
            badge = IconBadge.CreateError(errString);

            Add(badge);
            badge.AttachTo(outputContainer, SpriteAlignment.RightCenter);
        }

        public void ClearMessage()
        {
            var badge = this.Q<IconBadge>();
            if(badge != null)
            {
                badge.Detach();
                badge.RemoveFromHierarchy();
            }
        }

        public void SetColor(Color newColor)
        {

        }

        public void ResetColor()
        {

        }
        #endregion
    }
}
