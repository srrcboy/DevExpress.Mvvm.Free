using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.ServiceModel;
using System.Threading;
using System.Windows.Threading;
using DevExpress.Utils;
using DevExpress.Mvvm.Native;

namespace DevExpress.Mvvm.UI.Native {
    public interface IJumpAction {
        string CommandId { get; }
        string ApplicationPath { get; }
        string Arguments { get; }
        string WorkingDirectory { get; }
        void SetStartInfo(string applicationPath, string arguments);
        void Execute();
    }
    public interface IJumpActionsManager {
        void BeginUpdate();
        void EndUpdate();
        void RegisterAction(IJumpAction jumpAction, string commandLineArgumentPrefix, Func<string> launcherPath);
    }
    public class JumpActionsManager : JumpActionsManagerBase, IJumpActionsManager, IDisposable {
        static object factoryLock = new object();
        static Func<JumpActionsManager> factory = () => new JumpActionsManager();
        public static Func<JumpActionsManager> Factory {
            get { return factory; }
            set {
                lock(factoryLock) {
                    Guard.ArgumentNotNull(value, "value");
                    factory = value;
                }
            }
        }
        static JumpActionsManager current;
        public static JumpActionsManager Current {
            get {
                lock(factoryLock) {
                    if(current == null) {
                        current = Factory();
                        if(current == null)
                            throw new InvalidOperationException();
                    }
                    return current;
                }
            }
        }
        protected class RegisteredJumpAction {
            WeakReference taskReference;

            public RegisteredJumpAction(IJumpAction jumpAction) {
                Id = jumpAction.CommandId;
                taskReference = new WeakReference(jumpAction);
                Dispatcher = Dispatcher.CurrentDispatcher;
            }
            public string Id { get; private set; }
            public Dispatcher Dispatcher { get; private set; }
            public IJumpAction GetJumpAction() { return taskReference == null ? null : (IJumpAction)taskReference.Target; }
        }
        [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single)]
        class ApplicationInstance : IApplicationInstance {
            readonly JumpActionsManager manager;

            public ApplicationInstance(JumpActionsManager manager) {
                this.manager = manager;
            }
            void IApplicationInstance.Execute(string command) {
                manager.ExecuteCore(command);
            }
        }

        readonly Dictionary<string, RegisteredJumpAction> jumpActions = new Dictionary<string, RegisteredJumpAction>();
        GuidData applicationInstanceId;
        ServiceHost applicationInstanceHost;
        bool registered = false;
        ICurrentProcess currentProcess;
        bool disposed = false;
        bool updating = false;

        public JumpActionsManager(ICurrentProcess currentProcess = null, int millisecondsTimeout = DefaultMillisecondsTimeout)
            : base(millisecondsTimeout) {
            this.currentProcess = currentProcess ?? new CurrentProcess();
        }
        ~JumpActionsManager() {
            Dispose(false);
        }
        public void Dispose() {
            if(disposed) return;
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        protected virtual void Dispose(bool disposing) {
            if(disposing)
                Monitor.Enter(jumpActions);
            try {
                Mutex mainMutex = GetMainMutex(!disposing);
                WaitOne(mainMutex);
                try {
                    UnregisterInstance(!disposing);
                } finally {
                    mainMutex.ReleaseMutex();
                }
            } finally {
                if(disposing)
                    Monitor.Exit(jumpActions);
            }
        }
        public void BeginUpdate() {
            if(updating)
                throw new InvalidOperationException();
            Monitor.Enter(jumpActions);
            try {
                Mutex mainMutex = GetMainMutex(false);
                WaitOne(mainMutex);
                try {
                    ClearActions();
                } catch {
                    mainMutex.ReleaseMutex();
                    throw;
                }
            } catch {
                Monitor.Exit(jumpActions);
                throw;
            }
            updating = true;
        }
        public void EndUpdate() {
            if(!updating)
                throw new InvalidOperationException();
            try {
                Mutex mainMutex = GetMainMutex(false);
                mainMutex.ReleaseMutex();
            } finally {
                updating = false;
                Monitor.Exit(jumpActions);
            }
        }
        [SecuritySafeCritical]
        public void RegisterAction(IJumpAction jumpAction, string commandLineArgumentPrefix, Func<string> launcherPath) {
            Guard.ArgumentNotNull(jumpAction, "jumpAction");
            if(!updating)
                throw new InvalidOperationException();
            RegisterInstance();
            RegisteredJumpAction registeredjumpAction = PrepareJumpActionToRegister(jumpAction, commandLineArgumentPrefix, launcherPath);
            AddAction(registeredjumpAction);
            if(ShouldExecute(jumpAction.CommandId, commandLineArgumentPrefix))
                ExecuteCore(jumpAction.CommandId);
        }
        protected virtual RegisteredJumpAction PrepareJumpActionToRegister(IJumpAction jumpAction, string commandLineArgumentPrefix, Func<string> launcherPath) {
            RegisteredJumpAction registeredJumpAction = new RegisteredJumpAction(jumpAction);
            string exePath = jumpAction.ApplicationPath;
            string applicationArguments = jumpAction.Arguments ?? string.Empty;
            if(string.IsNullOrEmpty(exePath))
                exePath = currentProcess.ExecutablePath;
            if(string.Equals(exePath, currentProcess.ExecutablePath, StringComparison.OrdinalIgnoreCase)) {
                string actionArg = commandLineArgumentPrefix + Uri.EscapeDataString(registeredJumpAction.Id);
                applicationArguments = string.IsNullOrEmpty(applicationArguments) ? actionArg : applicationArguments + actionArg;
            }
            string launcherArguments = string.Join(" ",
                currentProcess.ApplicationId,
                Uri.EscapeDataString(registeredJumpAction.Id),
                Uri.EscapeDataString(exePath),
                string.Format("\"{0}\"", Uri.EscapeDataString(applicationArguments))
            );
            if(!string.IsNullOrEmpty(jumpAction.WorkingDirectory))
                launcherArguments = string.Join(" ", launcherArguments, Uri.EscapeDataString(jumpAction.WorkingDirectory));
            jumpAction.SetStartInfo(launcherPath(), launcherArguments);
            return registeredJumpAction;
        }
        protected override string ApplicationId { get { return currentProcess.ApplicationId; } }
        bool ShouldExecute(string command, string commandLineArgumentPrefix) {
            string arg = commandLineArgumentPrefix + Uri.EscapeDataString(command);
            return currentProcess.CommandLineArgs.Skip(1).Where(a => string.Equals(a, arg)).Any();
        }
        void ExecuteCore(string command) {
            RegisteredJumpAction registeredJumpAction;
            if(!jumpActions.TryGetValue(command, out registeredJumpAction)) return;
            IJumpAction jumpAction = registeredJumpAction.GetJumpAction();
            if(jumpAction == null)
                jumpActions.Remove(command);
            else
                registeredJumpAction.Dispatcher.BeginInvoke((Action)jumpAction.Execute);
        }
        void AddAction(RegisteredJumpAction jumpAction) {
            jumpActions[jumpAction.Id] = jumpAction;
        }
        void ClearActions() {
            jumpActions.Clear();
        }
        void RegisterInstance(GuidData[] registeredApplicationInstances = null) {
            if(registered) return;
            if(registeredApplicationInstances == null)
                registeredApplicationInstances = GetApplicationInstances(false);
            CreateInstance();
            GuidData[] newApplicationInstances = new GuidData[registeredApplicationInstances.Length + 1];
            newApplicationInstances[0] = applicationInstanceId;
            Array.Copy(registeredApplicationInstances, 0, newApplicationInstances, 1, registeredApplicationInstances.Length);
            UpdateInstancesFile(newApplicationInstances, false);
            registered = true;
        }
        void CreateInstance() {
            applicationInstanceId = new GuidData(Guid.NewGuid());
            applicationInstanceHost = new ServiceHost(new ApplicationInstance(this), new Uri(GetServiceUri(applicationInstanceId)));
            applicationInstanceHost.AddServiceEndpoint(typeof(IApplicationInstance), new NetNamedPipeBinding(), EndPointName);
            applicationInstanceHost.Open(new TimeSpan(0, 0, 0, 0, MillisecondsTimeout));
        }
        void DeleteInstance() {
            applicationInstanceHost.Close(new TimeSpan(0, 0, 0, 0, MillisecondsTimeout));
            applicationInstanceHost = null;
            applicationInstanceId = new GuidData(Guid.Empty);
        }
        void UnregisterInstance(bool safe) {
            if(!registered) return;
            GuidData[] registeredApplicationInstances = GetApplicationInstances(safe);
            GuidData[] newApplicationInstances = new GuidData[registeredApplicationInstances.Length - 1];
            int i = 0;
            foreach(GuidData instance in registeredApplicationInstances) {
                if(instance.AsGuid == applicationInstanceId.AsGuid) continue;
                newApplicationInstances[i] = instance;
                ++i;
            }
            UpdateInstancesFile(newApplicationInstances, safe);
            registered = false;
            if(!safe && applicationInstanceHost != null)
                DeleteInstance();
        }
    }
}