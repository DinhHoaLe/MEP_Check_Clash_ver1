using System;
using Autodesk.Revit.UI;

namespace MEP_Check_Clash_ver1
{
    // Every modeless API action is marshalled back into a Revit API context.
    public sealed class RevitRequest : IExternalEventHandler, IDisposable
    {
        private Action<UIApplication> pending;
        private readonly ExternalEvent externalEvent;
        private readonly Action<Exception> onError;
        private bool disposed;
        public RevitRequest(Action<Exception> onError)
        {
            this.onError = onError;
            externalEvent = ExternalEvent.Create(this);
        }
        public void Post(Action<UIApplication> action)
        {
            if (disposed) return;
            if (pending != null) throw new InvalidOperationException("A Revit request is already pending.");
            pending = action;
            var result = externalEvent.Raise();
            if (result != ExternalEventRequest.Accepted && result != ExternalEventRequest.Pending)
            {
                pending = null;
                throw new InvalidOperationException("Revit did not accept the request: " + result);
            }
        }
        public void Execute(UIApplication app)
        {
            var action = pending;
            pending = null;
            if (disposed || action == null) return;
            try { action(app); } catch (Exception ex) { onError(ex); }
        }
        public string GetName() { return "Clash Solution C# requests"; }
        public void Dispose() { disposed = true; pending = null; externalEvent.Dispose(); }
    }
}
