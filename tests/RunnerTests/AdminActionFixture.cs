internal static class AdminActionFixture
{
    internal static void Run()
    {
        string id=Guid.NewGuid().ToString("N");
        bool Valid(NativeAdminAction action,string target,NativeAdminState before,
            bool allowDisruptive=false,bool readOnly=false,bool confirm=true) =>
            NativeAdminControl.Valid(action,target,id,before,allowDisruptive,readOnly,confirm);
        if(!Valid(NativeAdminAction.SERVICE_START,"FixtureService",NativeAdminState.STOPPED) ||
            !Valid(NativeAdminAction.SERVICE_STOP,"FixtureService",NativeAdminState.RUNNING,true) ||
            !Valid(NativeAdminAction.SERVICE_PAUSE,"FixtureService",NativeAdminState.RUNNING) ||
            !Valid(NativeAdminAction.SERVICE_CONTINUE,"FixtureService",NativeAdminState.PAUSED) ||
            !Valid(NativeAdminAction.TASK_ENABLE,"\\Fixture\\Example",NativeAdminState.DISABLED) ||
            !Valid(NativeAdminAction.TASK_DISABLE,"\\Fixture\\Example",NativeAdminState.ENABLED) ||
            !Valid(NativeAdminAction.TASK_DISABLE,"\\Fixture\\Example",NativeAdminState.ENABLED,
                readOnly:true,confirm:false) ||
            Valid(NativeAdminAction.SERVICE_STOP,"FixtureService",NativeAdminState.RUNNING) ||
            Valid(NativeAdminAction.SERVICE_START,"FixtureService",NativeAdminState.RUNNING) ||
            Valid(NativeAdminAction.SERVICE_START,"FixtureService",NativeAdminState.STOPPED,confirm:false) ||
            Valid(NativeAdminAction.TASK_DISABLE,"\\Fixture\\..\\Task",NativeAdminState.ENABLED) ||
            Valid(NativeAdminAction.TASK_DISABLE,"\\Fixture\\\\Task",NativeAdminState.ENABLED) ||
            Valid(NativeAdminAction.SERVICE_START,"\\Remote",NativeAdminState.STOPPED) ||
            NativeAdminControl.Valid(NativeAdminAction.SERVICE_START,"FixtureService",
                "not-a-guid",NativeAdminState.STOPPED,false,false,true))
            throw new InvalidOperationException("FAIL issue72.native_admin_guard_contract");
        Console.WriteLine("PASS issue72.native_admin_guard_contract");

        // Every generated test target stays inside this exact disposable
        // directory. This verifies crash/retry accounting without touching SCM
        // or Task Scheduler on any runner or live customer machine.
        string root=Path.Combine(Path.GetTempPath(),"wmcp-admin-acceptance-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string actor="S-1-5-21-acceptance-fixture";
            string operation=Guid.NewGuid().ToString("N");
            int nativeCalls=0;
            NativeAdminResult Native(NativeAdminIntent intent,CancellationToken ct)
            {
                Interlocked.Increment(ref nativeCalls);
                Thread.Sleep(120); // make the duplicate intent race observable
                return new(NativeAdminCode.COMMITTED,intent.OperationId,intent.Action,intent.Target,
                    WindowEffectState.EFFECT_VERIFIED,"SIMULATED_TEST_ONLY",DateTimeOffset.UtcNow,
                    intent.ExpectedState,NativeAdminState.RUNNING);
            }
            NativeAdminResult Run(bool readOnly=false,string? target=null) =>
                NativeAdminControl.Run(NativeAdminAction.SERVICE_START,target??"FixtureService",operation,
                    NativeAdminState.STOPPED,false,readOnly,!readOnly,actor,CancellationToken.None,
                    root,Native);
            var absent=Run(readOnly:true);
            if(absent.Status!=NativeAdminCode.NOT_FOUND || nativeCalls!=0 ||
                Directory.EnumerateFiles(root).Any())
                throw new InvalidOperationException("FAIL issue72.admin_receipt_read_only_absent");
            Console.WriteLine("PASS issue72.admin_receipt_read_only_absent");

            var contenders=Enumerable.Range(0,8).Select(_=>Task.Run(()=>Run())).ToArray();
            Task.WaitAll(contenders);
            if(nativeCalls!=1 || contenders.Any(t=>t.Result.Status is not
                    (NativeAdminCode.COMMITTED or NativeAdminCode.EFFECT_UNKNOWN)))
                throw new InvalidOperationException("FAIL issue72.admin_receipt_concurrent_once");
            var replay=Run();
            var status=Run(readOnly:true);
            var conflict=Run(readOnly:true,target:"DifferentService");
            if(nativeCalls!=1 || replay.Status!=NativeAdminCode.COMMITTED ||
                status.Status!=NativeAdminCode.COMMITTED || conflict.Status!=NativeAdminCode.CONFLICT)
                throw new InvalidOperationException("FAIL issue72.admin_receipt_replay_and_identity");
            Console.WriteLine("PASS issue72.admin_receipt_concurrent_once");
            Console.WriteLine("PASS issue72.admin_receipt_replay_and_identity");

            string unknownId=Guid.NewGuid().ToString("N");
            int uncertainCalls=0;
            NativeAdminResult Unknown(NativeAdminIntent intent,CancellationToken ct)
            {
                Interlocked.Increment(ref uncertainCalls);
                throw new OperationCanceledException("Simulated lost native acknowledgement");
            }
            NativeAdminResult UnknownRun(bool readOnly=false) => NativeAdminControl.Run(
                NativeAdminAction.SERVICE_START,"UnknownFixture",unknownId,NativeAdminState.STOPPED,
                false,readOnly,!readOnly,actor,CancellationToken.None,root,Unknown);
            if(UnknownRun().Status!=NativeAdminCode.EFFECT_UNKNOWN ||
                UnknownRun().Status!=NativeAdminCode.EFFECT_UNKNOWN ||
                UnknownRun(readOnly:true).Status!=NativeAdminCode.EFFECT_UNKNOWN || uncertainCalls!=1)
                throw new InvalidOperationException("FAIL issue72.admin_receipt_unknown_no_replay");
            Console.WriteLine("PASS issue72.admin_receipt_unknown_no_replay");
        }
        finally {Directory.Delete(root,recursive:true);}
    }
}
