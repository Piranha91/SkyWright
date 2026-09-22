"""Renderer-free readiness controls; the renderer RED is a separately gated launch."""
import hashlib, json, os, sys, tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import renderbatch as rb
import renderlimit as rl

FAIL = []
def check(ok, label):
    print(("  PASS  " if ok else "  FAIL  ") + label)
    if not ok: FAIL.append(label)

FAKE = r'''
import hashlib,json,pathlib,sys,time
root=pathlib.Path(sys.argv[1]); ready=sys.argv[2]; workhash=sys.argv[3]
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
a=root/'caller-c1s1-frame.png'; a.write_bytes(b'complete-a')
rpt=root/'caller-c1s1-report.json'; rpt.write_text('{}')
r={'schema':'viewprobe-shot-progress-v1','status':'complete','exitCode':0,
   'readinessSha256':ready,'worklistSha256':workhash,
   'coordinate':{'cell':0,'shot':0},'artifacts':[
       {'path':str(a),'kind':'frame','sha256':digest(a)},
       {'path':str(rpt),'kind':'report','sha256':digest(rpt)}],
   'evidenceMasks':[]}
t=root/'c1s1.json.tmp'; t.write_text(json.dumps(r)); t.replace(root/'c1s1.json')
(root/'caller-c1s2-frame.png.partial').write_bytes(b'partial-b')
print('BEGIN original c1s2',flush=True)
time.sleep(30)
'''

BATCH_FAKE = r'''
import hashlib,json,pathlib,sys
selection=pathlib.Path(sys.argv[1]); progress=pathlib.Path(sys.argv[2]); output=pathlib.Path(sys.argv[3])
ready=sys.argv[4]; workhash=sys.argv[5]
rows=json.loads(selection.read_text())['selected']; progress.mkdir(parents=True,exist_ok=True); output.mkdir(parents=True,exist_ok=True)
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
chosen=rows[:1] if len(rows)>1 else rows
for c in chosen:
  stem=f"c{c['cell']+1}s{c['shot']+1}"; frame=output/(stem+'-frame.png'); report=output/(stem+'-report.json')
  frame.write_bytes(stem.encode()); report.write_text('{}')
  receipt={'schema':'viewprobe-shot-progress-v1','status':'complete','exitCode':0,
    'readinessSha256':ready,'worklistSha256':workhash,'coordinate':c,
    'artifacts':[{'path':str(frame),'kind':'frame','sha256':digest(frame)},
                 {'path':str(report),'kind':'report','sha256':digest(report)}], 'evidenceMasks':[]}
  tmp=progress/(stem+'.json.tmp'); tmp.write_text(json.dumps(receipt)); tmp.replace(progress/(stem+'.json'))
if len(rows)>1:
  c=rows[1]; stem=f"c{c['cell']+1}s{c['shot']+1}"; (output/(stem+'-frame.png')).write_bytes(b'partial')
if len(sys.argv)>6 and len(rows)>1: pathlib.Path(sys.argv[6]).write_text('changed')
raise SystemExit(7 if len(rows)>1 else 0)
'''

BAD_RECEIPT_FAKE = r'''
import json,pathlib,sys
progress=pathlib.Path(sys.argv[1]); output=pathlib.Path(sys.argv[2]); progress.mkdir(); output.mkdir()
(output/'c1s1-frame.png').write_bytes(b'partial')
(progress/'c1s1.json').write_text(json.dumps({'status':'complete'}))
'''

def main():
    table = rb.load_key_table()
    check(len([x for x in table['options'] if not x.get('operational')]) == 201,
          "scrubbed table contains all 201 strategy rows")
    check(len([x for x in table['options'] if x.get('operational')]) == 2,
          "new selection/progress flags are explicitly classified")
    strategy=[x for x in table['options'] if not x.get('operational')]
    identity={rb.option_key(x):'same' for x in strategy}
    by_level={level:next(x for x in strategy if x['invalidates']==level)
              for level in ('process','configuration','cell','shot','none')}
    campaign=[]
    for shot,level in enumerate(('none','shot','cell','configuration','process')):
        row={'coordinate':{'cell':0,'shot':shot},'identity':dict(identity),'size':320+shot}
        if level!='none': row['identity'][rb.option_key(by_level[level])]=level
        campaign.append(row)
    groups=rb.group_campaign(campaign,table)
    check(sorted(len(g['items']) for g in groups)==[1,1,1,2],
          "all 201 keys group at their declared process/configuration/cell levels")
    bad=dict(campaign[0]); bad['identity']=dict(identity); bad['identity'].pop(next(iter(identity)))
    try: rb.group_campaign([bad],table); complete=False
    except ValueError: complete=True
    check(complete,"missing identity row refuses planning")
    nested=dict(campaign[0]); nested['identity']=dict(identity)
    nested['identity'][rb.option_key(strategy[0])]={'paths':['a','b'],'enabled':True}
    try: rb.group_campaign([nested],table); nested_ok=True
    except TypeError: nested_ok=False
    check(nested_ok,"typed nested/list identity values canonicalize for grouping")
    check(callable(getattr(rb,'main',None)),"public runnable campaign adapter exists")
    with tempfile.TemporaryDirectory(prefix="hc-renderbatch-test-") as td:
        root=Path(td); worklist=root/'original.json'
        old_log=os.environ.get('HOUSECARL_RUNLOG_DIR')
        os.environ['HOUSECARL_RUNLOG_DIR']=str(root/'runlog')
        worklist.write_text(json.dumps({'Cells':[{'Cell':'A','Shots':[
            {'Label':'one'},{'Label':'two'},{'Label':'three'}]}]}))
        wh=rb.sha256(worklist); ready='A'*64
        identity_file=root/'identity.bin'; identity_file.write_bytes(b'one')
        pinned=rb.input_state([fake if 'fake' in locals() else worklist,identity_file],['RB_TEST_ENV'])
        rb.require_unchanged(pinned,[worklist,identity_file],['RB_TEST_ENV'])
        identity_file.write_bytes(b'two')
        try: rb.require_unchanged(pinned,[worklist,identity_file],['RB_TEST_ENV']); stale=False
        except RuntimeError: stale=True
        check(stale,"changed input identity refuses before launch or resume")
        identity_file.write_bytes(b'one')
        plan_manifest=root/'plan-manifest.json'; plan_out=root/'plan.json'
        plan_manifest.write_text(json.dumps({'mode':'plan','items':[campaign[0]],
            'need':{'graphicsMiB':1,'systemMiB':1,'source':'test'},'askedParallel':1,
            'reserveMiB':1,'statePaths':[str(worklist)],'stateEnvironment':[],
            'run':{'argv':['fake'],'worklist':str(worklist),'expectedArtifacts':{}}}))
        plan_exit=rb.main([str(plan_manifest),'--json',str(plan_out)])
        planned=json.loads(plan_out.read_text())
        check(plan_exit==0 and len(planned.get('computedReadinessSha256',''))==64
              and planned.get('actualConcurrency') is None,
              "plan output publishes the computed readiness identity without launching")
        doc=rb.selection(worklist,ready,[(0,1),(0,2)])
        check(doc['selected']==[{'cell':0,'shot':1},{'cell':0,'shot':2}],
              "selection retains original coordinates without renumbering")
        retained=Path(r'C:/Users/Piranha/AppData/Local/Temp/hc-rt-s4/wl-a.json')
        retained_data=json.loads(retained.read_text())
        lowercase=root/'retained-lowercase.json'
        lowercase.write_text(json.dumps({'label':retained_data.get('Label'),'cells':[
            {**{k:v for k,v in c.items() if k not in ('Cell','Shots')},'cell':c['Cell'],
             'shots':[{**{k:v for k,v in s.items() if k!='Label'},'label':s.get('Label','')}
                      for s in c['Shots']]} for c in retained_data['Cells']]}))
        try: _,retained_rows=rb.worklist_shape(lowercase); retained_lower=bool(retained_rows)
        except (OSError,ValueError,KeyError,TypeError): retained_lower=False
        check(retained_lower,"real retained lowercase cells/shots/label worklist is accepted")
        refused=0
        for coords in ([(0,1),(0,1)],[(0,9)],[(False,1)],[(0,1.5)],[(0,'1')]):
            try: rb.selection(worklist,ready,coords)
            except ValueError: refused+=1
        check(refused==5,"duplicate, range, bool, fractional and string coordinates refuse")
        attempts=root/'attempts'; first=rb.attempt_dir(attempts,'job',1)
        try: rb.attempt_dir(attempts,'job',1); overwrite=True
        except FileExistsError: overwrite=False
        check(first.is_dir() and not overwrite,"attempt evidence never overwrites")
        contained=0
        for logical,attempt in (('../escape',1),('job',0),('job',True)):
            try: rb.attempt_dir(attempts,logical,attempt)
            except ValueError: contained+=1
            except FileExistsError: pass
        check(contained==3,"attempt logical id and number cannot escape or alias evidence paths")
        selection_path=root/'selection.json'; rb.write_selection(selection_path,doc)
        try: rb.write_selection(selection_path,doc); selection_overwrite=True
        except FileExistsError: selection_overwrite=False
        tmp_path=root/'blocked.json'; (root/'blocked.json.tmp').write_text('foreign')
        try: rb.write_selection(tmp_path,doc); tmp_overwrite=True
        except FileExistsError: tmp_overwrite=False
        check(not selection_overwrite and not tmp_overwrite,
              "selection publication refuses existing destination and temporary paths")
        fake=root/'fake.py'; fake.write_text(FAKE)
        def cap(event):
            if event.get('kind')=='output' and 'BEGIN original c1s2' in event.get('text',''):
                return {'stop':'process-commit-cap','message':'test cap','metrics':{'commitBytes':1}}
        result=rb.Runner('renderbatch-fake-kill',out_dir=root/'logs').run([
            {'id':'chunk','logicalId':'job.chunk','argv':[sys.executable,str(fake),str(root),ready,wh]}
        ],echo=False,observer=cap,poll_seconds=.02)[0]
        check(result.get('stopped')=='observer' and result.get('treeStopped') is True
              and result.get('stopReason')=='process-commit-cap',
              "real fake child crosses cap and its owned tree is stopped")
        cap_events=[]
        cap=rb.cap_observer(lambda pid: (cap_events.append(pid) or 9.0),8.0)
        cap_result=rb.Runner('renderbatch-real-cap',out_dir=root/'cap-logs').run([
            {'id':'cap','logicalId':'job.cap','argv':[sys.executable,'-c','import time; time.sleep(30)']}
        ],echo=False,observer=cap,poll_seconds=.02)[0]
        check(bool(cap_events) and cap_result.get('stopReason')=='process-commit-cap'
              and cap_result.get('treeStopped') is True,
              "monitored sample makes a real cap decision and stops the owned tree")
        actual_cap=rb.cap_observer(rb.process_commit_mib,.0001)
        actual_result=rb.Runner('renderbatch-actual-commit',out_dir=root/'actual-cap-logs').run([
            {'id':'actual-cap','logicalId':'job.actual-cap','argv':[sys.executable,'-c','import time; time.sleep(30)']}
        ],echo=False,observer=actual_cap,poll_seconds=.02)[0]
        check(actual_result.get('stopReason')=='process-commit-cap' and actual_result.get('treeStopped') is True,
              "public owned-PID commit reader makes an actual cap stop")
        unavailable=rb.cap_observer(lambda pid: None,8.0)({'kind':'sample','ownedPid':123})
        check(unavailable.get('stop')=='telemetry-unavailable',
              "missing required process telemetry stops instead of becoming zero")
        negative=rb.cap_observer(lambda pid:-1,8.0)({'kind':'sample','ownedPid':123})
        check(isinstance(negative,dict) and negative.get('stop')=='telemetry-unavailable',
              "negative process telemetry is unavailable, never below-cap success")
        multi=rb.cap_observer(lambda pid:1,8,system_reader=lambda:rl.Reading(rl.SYSTEM,2,10,'test',True),
                              system_available_floor_mib=3)
        check(multi({'kind':'sample','ownedPid':123}).get('stop')=='system-memory-cap',
              "system floor participates in the public cap observer")
        pre=multi({'kind':'prelaunch','jobId':'x'})
        check(isinstance(pre,dict) and pre.get('stop')=='system-memory-cap',
              "required system/graphics caps are checked before child launch")
        outputs=[{'path':str(root/'caller-c1s1-frame.png'),'kind':'frame'},
                 {'path':str(root/'caller-c1s1-report.json'),'kind':'report'}]
        ok,row=rb.validate_receipt(root/'c1s1.json',readiness_sha256=ready,
                                   worklist_sha256=wh,coordinate=(0,0),
                                   expected_artifacts=outputs)
        check(ok,"atomic receipt with exact declared frame/report outputs is accepted")
        malformed=dict(row); malformed.pop('schema'); malformed['artifacts']=malformed['artifacts'][:1]
        bad=root/'malformed.json'; bad.write_text(json.dumps(malformed))
        check(not rb.validate_receipt(bad,readiness_sha256=ready,worklist_sha256=wh,
                                      coordinate=(0,0),expected_artifacts=outputs)[0],
              "receipt missing schema and a declared output is refused")
        check(not (root/'c1s2.json').exists(),"partial caller artifact is not completion")
        plan=rb.resume_after_failed_chunk([(0,0),(0,1),(0,2)],{(0,0)})
        check(plan=={'later':[(0,2)],'culpritAlone':[(0,1)]},
              "resume runs later C then culprit B alone once")
        resume,failures=rb.next_resume([(0,0),(0,1),(0,2)],root,
            readiness_sha256=ready,worklist_sha256=wh,
            expected_outputs=lambda c: outputs if c==(0,0) else [])
        check(resume==plan and not failures,"resume advances only from exact validated receipts")
        batch_fake=root/'batch-fake.py'; batch_fake.write_text(BATCH_FAKE)
        batch_progress=root/'batch-progress'; batch_output=root/'batch-output'
        state=rb.input_state([batch_fake,worklist])
        def batch_outputs(coord):
            stem=f'c{coord[0]+1}s{coord[1]+1}'
            return [{'path':str(batch_output/(stem+'-frame.png')),'kind':'frame'},
                    {'path':str(batch_output/(stem+'-report.json')),'kind':'report'}]
        def batch_argv(selection_path,attempt_path):
            return [sys.executable,str(batch_fake),str(selection_path),str(batch_progress),
                    str(batch_output),ready,wh]
        batch=rb.execute_group(logical_id='cell-a',ordered=[(0,0),(0,1),(0,2)],
            worklist=worklist,readiness_sha256=ready,receipt_dir=batch_progress,
            expected_outputs=batch_outputs,state=state,state_paths=[batch_fake,worklist],
            state_environment=(),attempt_root=root/'batch-attempts',argv_for=batch_argv,
            observer=lambda event: None,shots_per_process=3,
            authorized_output_roots=[batch_output],runner_name='renderbatch-resume')
        check(batch['status']=='complete'
              and [x['selected'] for x in batch['attempts']]==[[(0,0),(0,1),(0,2)],[(0,2)],[(0,1)]],
              "failed chunk evidence is retained; later shots run before culprit alone exactly once")
        partial=list((root/'batch-attempts'/'cell-a'/'attempt-001'/'partial-artifacts'/'c1s2').rglob('*frame.png'))
        check(len(partial)==1 and partial[0].read_bytes()==b'partial'
              and (batch_output/'c1s2-frame.png').read_bytes()==b'c1s2',
              "partial caller output is quarantined while successful retry keeps its original name")
        try:
            resumed=rb.execute_group(logical_id='cell-a',ordered=[(0,0),(0,1),(0,2)],
                worklist=worklist,readiness_sha256=ready,receipt_dir=batch_progress,
                expected_outputs=batch_outputs,state=state,state_paths=[batch_fake,worklist],
                state_environment=(),attempt_root=root/'batch-attempts',argv_for=batch_argv,
                observer=lambda event:None,shots_per_process=3,
                authorized_output_roots=[batch_output],runner_name='renderbatch-resume')
            durable_resume=resumed['status']=='complete' and not resumed['attempts']
        except FileExistsError: durable_resume=False
        check(durable_resume,"fresh driver invocation resumes from durable state without attempt reset")
        identity_file.write_bytes(b'changed-after-receipts')
        try:
            rb.execute_group(logical_id='cell-a',ordered=[(0,0),(0,1),(0,2)],worklist=worklist,
                readiness_sha256=ready,receipt_dir=batch_progress,expected_outputs=batch_outputs,
                state=state,state_paths=[batch_fake,worklist,identity_file],state_environment=(),
                attempt_root=root/'batch-attempts',argv_for=batch_argv,observer=lambda event:None,
                shots_per_process=3,authorized_output_roots=[batch_output])
            receipt_state_refused=False
        except RuntimeError: receipt_state_refused=True
        check(receipt_state_refused,"input state is checked before receipt-only completion")
        identity_file.write_bytes(b'one')
        partial_source=root/'authorized-output'/'nested'/'mask.png'; partial_source.parent.mkdir(parents=True)
        partial_source.write_bytes(b'mask')
        try:
            moved=rb.quarantine_outputs((0,1),[{'path':str(partial_source),'kind':'mask'}],
                root/'audit',authorized_output_roots=[root/'authorized-output'])
            quarantine_safe=bool(moved) and 'nested' in moved[0]['auditPath']
        except TypeError: quarantine_safe=False
        check(quarantine_safe,"quarantine authorizes output roots and preserves relative directories")
        recover_progress=root/'recover-progress'; recover_output=root/'recover-output'
        recover_output.mkdir(); (recover_output/'c1s1-frame.png').write_bytes(b'interrupted')
        recover_root=root/'recover-attempts'; recover_ad=recover_root/'recover-cell'/'attempt-001'
        recover_ad.mkdir(parents=True)
        schedule={'schema':'viewprobe-renderbatch-schedule-v1','logicalId':'recover-cell',
            'ordered':[[0,0],[0,1]],'readinessSha256':ready,'worklistSha256':wh,
            'pending':[],'isolated':[],'lastAttempt':1,
            'inFlight':{'attempt':1,'selected':[[0,0],[0,1]],'attemptDirectory':str(recover_ad)}}
        schedule['runIdentity']=hashlib.sha256(rb.canonical_value({'state':state,
            'ordered':[[0,0],[0,1]],'readiness':ready}).encode()).hexdigest().upper()
        (recover_ad.parent/'schedule.json').write_text(json.dumps(schedule))
        def recover_outputs(coord):
            stem=f'c{coord[0]+1}s{coord[1]+1}'
            return [{'path':str(recover_output/(stem+'-frame.png')),'kind':'frame'},
                    {'path':str(recover_output/(stem+'-report.json')),'kind':'report'}]
        def recover_argv(selection_path,attempt_path):
            return [sys.executable,str(batch_fake),str(selection_path),str(recover_progress),
                    str(recover_output),ready,wh]
        recovered=rb.execute_group(logical_id='recover-cell',ordered=[(0,0),(0,1)],worklist=worklist,
            readiness_sha256=ready,receipt_dir=recover_progress,expected_outputs=recover_outputs,
            state=state,state_paths=[batch_fake,worklist],state_environment=(),attempt_root=recover_root,
            argv_for=recover_argv,observer=lambda event:None,shots_per_process=2,
            authorized_output_roots=[recover_output],runner_name='renderbatch-recover')
        check(recovered.get('status')=='recovery-refused',
              "unresolved in-flight owner refuses recovery until the prior tree is proven gone")
        bad_fake=root/'bad-receipt.py'; bad_fake.write_text(BAD_RECEIPT_FAKE)
        bad_progress=root/'bad-progress'; bad_output=root/'bad-output'
        bad_state=rb.input_state([bad_fake,worklist])
        try:
            rb.execute_group(logical_id='bad-receipt',ordered=[(0,0)],worklist=worklist,
                readiness_sha256=ready,receipt_dir=bad_progress,
                expected_outputs=lambda c:[{'path':str(bad_output/'c1s1-frame.png'),'kind':'frame'}],
                state=bad_state,state_paths=[bad_fake,worklist],state_environment=(),
                attempt_root=root/'bad-attempts',argv_for=lambda s,a:[sys.executable,str(bad_fake),
                    str(bad_progress),str(bad_output)],observer=lambda event:None,shots_per_process=1,
                authorized_output_roots=[bad_output])
            malformed_refused=False
        except RuntimeError: malformed_refused=True
        check(malformed_refused and len(list((root/'bad-attempts'/'bad-receipt').glob('attempt-*')))==1,
              "malformed child receipt refuses immediately without quarantine/retry")
        check(callable(getattr(rb,'result_exit_code',None)) and rb.result_exit_code([{'status':'failed'}])!=0,
              "public adapter returns nonzero when any run fails")
        check(callable(getattr(rb,'validate_run_shape',None)) and
              not rb.validate_run_shape([{'items':[]},{'items':[]}])[0],
              "public adapter explicitly refuses unsupported multi-group launch shapes")
        stale_progress=root/'stale-progress'; stale_output=root/'stale-output'; state_file=root/'state.pin'
        state_file.write_text('initial'); stale_state=rb.input_state([batch_fake,worklist,state_file])
        def stale_outputs(coord):
            stem=f'c{coord[0]+1}s{coord[1]+1}'
            return [{'path':str(stale_output/(stem+'-frame.png')),'kind':'frame'},
                    {'path':str(stale_output/(stem+'-report.json')),'kind':'report'}]
        def stale_argv(selection_path,attempt_path):
            return [sys.executable,str(batch_fake),str(selection_path),str(stale_progress),
                    str(stale_output),ready,wh,str(state_file)]
        try:
            rb.execute_group(logical_id='stale-cell',ordered=[(0,0),(0,1)],worklist=worklist,
                readiness_sha256=ready,receipt_dir=stale_progress,expected_outputs=stale_outputs,
                state=stale_state,state_paths=[batch_fake,worklist,state_file],state_environment=(),
                attempt_root=root/'stale-attempts',argv_for=stale_argv,
                observer=lambda event:None,shots_per_process=2,
                authorized_output_roots=[stale_output],runner_name='renderbatch-stale')
            stale_blocked=False
        except RuntimeError: stale_blocked=True
        check(stale_blocked and len(list((root/'stale-attempts'/'stale-cell').glob('attempt-*')))==1,
              "input mutation after a killed chunk blocks the next resume launch")
        (root/'caller-c1s1-frame.png').write_bytes(b'changed')
        check(not rb.validate_receipt(root/'c1s1.json',readiness_sha256=ready,
                                      worklist_sha256=wh,coordinate=(0,0),
                                      expected_artifacts=outputs)[0],
              "changed artifact makes receipt stale")
        g=rl.Reading(rl.GRAPHICS,8000,12000,'test',True)
        s=rl.Reading(rl.SYSTEM,10000,16000,'test',True)
        limit=rb.plan_limit(8,rb.finite_need(2000,3000),g,s,reserve_mib=1000)
        check(limit.limit==3 and not limit.refused,"both memories bound concurrency")
        try: rb.finite_need(float('nan'),1); finite=False
        except ValueError: finite=True
        check(finite,"non-finite need cannot become zero or success")
        if old_log is None: os.environ.pop('HOUSECARL_RUNLOG_DIR',None)
        else: os.environ['HOUSECARL_RUNLOG_DIR']=old_log
    if FAIL: print(f"FAIL: {len(FAIL)} check(s)"); return 1
    print("GREEN: renderer-free renderbatch readiness"); return 0

if __name__=='__main__': raise SystemExit(main())
