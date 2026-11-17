import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

// M0 design validation only. This does not run an extractor or validate its output.
const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
const read = name => JSON.parse(fs.readFileSync(path.join(here, name), 'utf8'));
const requirements = read('requirements.json');
const dictionary = read('field-dictionary.json');
const documentContracts = read('contracts.json');
const source = fs.readFileSync(path.join(root, requirements.source.path), 'utf8');
const schema = fs.readFileSync(path.join(here, 'schema.sql'));
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const expectedSections = [...Array.from({length:16}, (_,i)=>`BD0.${i+1}`), ...Array.from({length:7}, (_,i)=>`BDG.${i+1}`), ...Array.from({length:10}, (_,i)=>`BDB.${i+1}`)];
const assert = (test, message) => { if (!test) throw new Error(message); };

const python = String.raw`
import sqlite3,json,sys,pathlib
sql=pathlib.Path(sys.argv[1]).read_text(encoding='utf8')
db=sqlite3.connect(':memory:')
db.executescript(sql)
tables=[]
for (name,) in db.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"):
    columns=[{'name':c[1],'type':c[2],'required':bool(c[3] or c[5]),'pk':c[5]} for c in db.execute('PRAGMA table_info('+name+')')]
    tables.append({'name':name,'columns':columns})
print(json.dumps({'tables':tables,'foreign_key_errors':list(db.execute('PRAGMA foreign_key_check')),'integrity':db.execute('PRAGMA integrity_check').fetchone()[0],'sqlite_version':sqlite3.sqlite_version}))
`;
const run = spawnSync(process.env.M0_PYTHON || 'python', ['-c', python, path.join(here,'schema.sql')], {encoding:'utf8', windowsHide:true});
assert(!run.error && run.status === 0, `Schema introspection failed: ${run.error?.message || run.stderr}`);
const ddl = JSON.parse(run.stdout);
assert(ddl.integrity === 'ok' && ddl.foreign_key_errors.length === 0, 'Empty-schema integrity failed');

function validate(req, dict) {
  assert(req.source.sha256 === hash(source), 'Inventory source fingerprint differs');
  assert(dict.schema_sha256 === hash(schema), 'Field dictionary is stale relative to DDL bytes');
  const ids = new Set(); const lines = new Set();
  const sourceLines = source.split('\n');
  const sections = new Map(req.sections.map(s=>[s.id,s]));
  assert(sections.size === 33 && expectedSections.every(s=>sections.has(s)), 'Missing or extra inventory section');
  const fields = new Map(dict.fields.map(f=>[f.id,f]));
  const producers = new Set(dict.producers.map(p=>p.id));
  assert(fields.size === dict.fields.length, 'Duplicate field dictionary ID');
  const actual = new Map(ddl.tables.flatMap(t=>t.columns.map(c=>[`${t.name}.${c.name}`,c])));
  assert(actual.size === fields.size, 'DDL/dictionary field counts differ');
  assert(new Set(dict.tables.map(t=>t.table)).size === ddl.tables.length, 'DDL/dictionary table counts differ');
  for (const [id,c] of actual) {
    const f=fields.get(id);
    assert(f, `Missing dictionary field ${id}`);
    assert(f.storage.table+'.'+f.storage.column===id && f.storage.sqlite_type===c.type && f.storage.nullable===!c.required && (f.storage.primary_key_ordinal||0)===c.pk, `Incorrect DDL contract ${id}`);
    for (const key of ['meaning','value_type','units','multiplicity','ordering','applicability','missing_states','producer','owner','evidence','validation','implementation_stage']) assert(f[key] && (!Array.isArray(f[key]) || f[key].length), `Incomplete ${key}: ${id}`);
    assert(f.implementation_status==='not_implemented', `M0 field falsely claims implementation: ${id}`);
    assert(producers.has(f.producer), `Unknown producer ${f.producer}: ${id}`);
    assert(f.missing_states.includes('known'), `Field missing known state: ${id}`);
    if (!c.required) for (const state of ['unknown','not_applicable','not_collected']) assert(f.missing_states.includes(state), `Missing ${state}: ${id}`);
  }
  for (const u of req.requirements) {
    assert(!ids.has(u.id), `Duplicate requirement ID ${u.id}`); ids.add(u.id);
    assert(!lines.has(u.source.line), `Duplicate source line ${u.source.line}`); lines.add(u.source.line);
    assert(u.source.path===req.source.path && u.source.sha256===req.source.sha256 && sourceLines[u.source.line-1]===u.source.text, `Source anchor changed: ${u.id}`);
    assert(['source_context','field_contract','normative_constraint'].includes(u.status), `Unresolved disposition: ${u.id}`);
    assert(u.contract_refs.length>0, `No normative contract: ${u.id}`);
    if (u.kind==='item' && sections.has(u.section)) assert(u.fields.length>0, `Unmapped inventory item: ${u.id}`);
    if (u.status==='field_contract') assert(u.fields.length>0, `Empty field contract: ${u.id}`);
    for (const field of u.fields) assert(fields.has(field), `Unknown column ${field} in ${u.id}`);
    if (sections.has(u.section)) {
      const s=sections.get(u.section);
      for (const key of ['producer','tasks','main_implementation_stage','acceptance','fixtures','validation','retrieval','collection_prerequisites','support_status']) assert(s[key] && (!Array.isArray(s[key])||s[key].length), `Incomplete section ${u.section}: ${key}`);
    } else assert(u.acceptance?.length, `Global rule lacks acceptance mapping: ${u.id}`);
  }
  for (let i=0;i<sourceLines.length;i++) if(sourceLines[i].trim()) assert(lines.has(i+1), `Unaccounted inventory source line ${i+1}`);
  for (const f of dict.fields) {
    const expected=req.requirements.filter(u=>u.fields.includes(f.id)).map(u=>u.id).sort();
    assert(JSON.stringify([...f.inventory_requirements].sort())===JSON.stringify(expected), `Reverse traceability differs: ${f.id}`);
  }
  const collectionIds=new Set();
  for (const col of dict.collections) {
    assert(!collectionIds.has(col.id), `Duplicate collection ${col.id}`);collectionIds.add(col.id);
    assert(fields.has(col.owner_field || `${col.table}.${col.owner_column}`), `Collection owner does not resolve: ${col.id}`);
    assert(col.known_empty && col.state_path && col.ordering, `Incomplete collection state/order: ${col.id}`);
    for(const column of col.discriminator_columns || [])assert(fields.has(`${col.table}.${column}`), `Unknown collection discriminator ${col.id}:${column}`);
    for(const join of col.owner_joins || [])assert(join.length===2 && join.every(f=>fields.has(f)), `Unresolved collection owner join ${col.id}`);
    if(col.ordering_contract.kind==='ordered'){
      assert(fields.has(`${col.table}.${col.ordering_contract.index_column}`), `Unresolved order index ${col.id}`);
      for(const column of col.ordering_contract.partition_columns)assert(fields.has(`${col.table}.${column}`), `Unresolved order partition ${col.id}:${column}`);
    }
  }
  for(const id of ['collection.parameter.parameters','collection.appearance.appearances','collection.annotation.annotations','collection.documentation.documentation','collection.operation.operations'])assert(collectionIds.has(id), `Missing semantic child collection ${id}`);
  assert(dict.collections.find(c=>c.id==='collection.entity_detail')?.multiplicity==='0..1','entity_detail must be a singleton per entity');
  for(const [table,partition]of [['operation_child',['parent_id']],['operation_operand',['operation_id','role']],['action_io',['action_id','direction']]])assert(JSON.stringify(dict.collections.find(c=>c.table===table)?.ordering_contract.partition_columns)===JSON.stringify(partition),`Incorrect ordering partition ${table}`);
  assert(dict.collections.find(c=>c.table==='group_membership')?.discriminator_columns.includes('context_key'),'Group membership knowledge must be scoped by context key');
  const captures=req.requirements.find(u=>u.id==='BD0.5.T015');
  assert(captures.fields.includes('member_relation.member_id')&&captures.fields.includes('member_relation.role'),'Required captured-variable identities cannot live only in optional data flow');
  const captureRole=dict.role_contracts.find(r=>r.table==='member_relation'&&r.role==='capture');
  assert(captureRole?.section==='BD0.5'&&['P1','P2','P3','P4'].every(p=>captureRole.profiles.includes(p)),'Captured identities must survive every baseline profile');
  const extensionIds=new Set();
  for(const ext of dict.registered_extensions || []){
    assert(!extensionIds.has(ext.id),`Duplicate registered extension ${ext.id}`);extensionIds.add(ext.id);
    assert(ext.id===`${ext.schema_name}/${ext.schema_version}`,`Extension version differs ${ext.id}`);
    const definition=documentContracts.$defs[ext.schema_name];
    const branches=definition?.oneOf || [definition];
    assert(branches.length && branches.every(b=>b?.properties.schema.const===ext.id),`Unregistered extension schema ${ext.id}`);
    assert(ext.schema_ref===`contracts.json#/$defs/${ext.schema_name}`,`Extension schema reference differs ${ext.id}`);
    assert(branches.every(b=>ext.properties.length===Object.keys(b.properties).length && ext.properties.every(p=>b.properties[p.name] && b.required.includes(p.name))),`Incomplete extension property dictionary ${ext.id}`);
    assert(ext.storage.payload_field==='record_extension.payload_json' && ext.owner.join[0]==='record_extension.record_id' && fields.has(ext.owner.join[1]) && ext.owner.join[1]===`${ext.owner.table}.id`,`Wrong extension storage/owner ${ext.id}`);
    for(const field of ext.normalization_boundary.retained_fields)assert(fields.has(field),`Unknown normalized extension field ${field}`);
    const expected=req.requirements.filter(u=>(u.registered_extension_refs || []).includes(ext.id)).map(u=>u.id).sort();
    assert(JSON.stringify([...ext.inventory_requirements].sort())===JSON.stringify(expected),`Extension reverse traceability differs ${ext.id}`);
    assert(ext.implementation_status==='not_implemented',`M0 extension falsely claims implementation ${ext.id}`);
  }
  for(const u of req.requirements)for(const ext of u.registered_extension_refs || [])assert(extensionIds.has(ext),`Unknown registered extension ${ext}`);
  const native=req.requirements.find(u=>u.id==='BD0.9.F004');
  assert(native.registered_extension_refs?.includes('cpp-attribute/1') && native.fields.includes('record_extension.payload_json'), 'Native attribute spelling lacks a concrete registered mapping');
  const cppAttribute=dict.registered_extensions?.find(e=>e.id==='cpp-attribute/1');
  assert(cppAttribute?.properties.some(p=>p.name==='written_name') && cppAttribute.properties.some(p=>p.name==='namespace') && cppAttribute.properties.some(p=>p.name==='syntax'), 'Native attribute identity is incomplete');
  const builtinState=fields.get('annotation.attribute_type_id').language_cases?.find(c=>c.language==='C++'&&c.case==='builtin_attribute');
  assert(builtinState?.physical_value===null && builtinState.state==='not_applicable' && builtinState.reason==='builtin_attribute_has_no_type', 'Builtin native attribute must not invent an attribute class');
  const boundaryState=cppAttribute.normalization_boundary.builtin_attribute_type_state;
  assert(boundaryState.value===null && boundaryState.state==='not_applicable' && boundaryState.reason===builtinState.reason, 'Native extension type applicability differs');
  const identity=dict.registered_extensions?.find(e=>e.id==='identity-input/1');
  assert(identity?.owner.table==='record' && identity.inventory_requirements.includes('BD0.4.F001') && identity.inventory_requirements.includes('GLOBAL.REVIEW.T026'), 'Identity extension lacks original identity/validation traceability');
  assert(identity.profile_policy?.minimal_anchor==='omit' && identity.profile_policy.anchor_authorizes_retention===false, 'Identity preimages must not leak through minimal profile anchors');
  assert(documentContracts['x-profile-table-ownership'].tables.record_extension.anchor_authorizes_retention===false, 'Identity extension dictionary disagrees with projection ownership');
  const variants=documentContracts.$defs['identity-input'].oneOf;
  assert(identity.domain_variants.length===variants.length, 'Identity recipe dictionary is incomplete');
  for(const [index,branch]of variants.entries()){
    const domain=branch.properties.domain.const;
    const mapped=identity.domain_variants.find(v=>v.domain===domain);
    assert(mapped && mapped.schema_ref===`contracts.json#/$defs/identity-input/oneOf/${index}/properties/components` && JSON.stringify(mapped.component_schema)===JSON.stringify(branch.properties.components), `Identity components differ: ${domain}`);
    assert(JSON.stringify(mapped.local_key_parts)===JSON.stringify(documentContracts['x-identity-recipes'][domain].local_key_parts || []), `Identity local-key semantics differ: ${domain}`);
  }
  assert(identity.hash_recipe.params_argument_count===1 && identity.hash_recipe.trailing_newline===false && identity.hash_recipe.preimage==='canonical JSON [domain, componentsObject]', 'Identity helper/preimage contract differs');
  const snapshotPair=dict.tables.find(t=>t.table==='snapshot')?.registry_pair;
  assert(snapshotPair?.id_relation==='snapshot.id = record.id' && snapshotPair.kind==='snapshot' && snapshotPair.section==='BD0.1' && snapshotPair.context_independent===1 && snapshotPair.context_id===null && snapshotPair.snapshot_id==='self' && snapshotPair.identity_domain==='snapshot/1', 'Snapshot lacks concrete registry/state/evidence ownership');
  for (const s of req.sections) {
    assert(producers.has(s.producer), `Unknown section producer ${s.producer}: ${s.id}`);
    const headings=req.requirements.filter(u=>u.section===s.id && u.kind==='section');
    assert(headings.length===1, `Section heading coverage differs: ${s.id}`);
  }
  return {source_units:ids.size,sections:sections.size,physical_fields:fields.size,tables:ddl.tables.length,logical_collections:dict.collections.length,registered_extensions:extensionIds.size};
}

const counts=validate(requirements,dictionary);
const targets=new Set([...requirements.requirements.flatMap(u=>u.contract_refs),...dictionary.fields.flatMap(f=>f.contract_refs),...(dictionary.registered_extensions || []).flatMap(e=>e.contract_refs)]);
for (const target of targets) {
  const [file,anchor]=target.split('#');
  const absolute=path.resolve(here,file);
  assert(fs.existsSync(absolute), `Missing local contract link ${target}`);
  if(anchor) {
    const headings=fs.readFileSync(absolute,'utf8').split('\n').filter(l=>/^#{1,6} /.test(l)).map(l=>l.replace(/^#+ /,'').trim().toLowerCase().replace(/[^a-z0-9\s-]/g,'').replace(/\s+/g,'-'));
    assert(headings.includes(anchor), `Missing contract anchor ${target}`);
  }
}
const contracts=fs.readFileSync(path.join(here,'contracts.md'),'utf8');
const operations=new Set([...contracts.matchAll(/^\| `([^`]+)`/gm)].map(m=>m[1]));
operations.add('callees');
for (const s of requirements.sections) for (const op of s.retrieval) assert(operations.has(op), `Undeclared query operation ${op} for ${s.id}`);
const acceptance=read('acceptance.json');
const checkIds=new Set(acceptance.checks.map(c=>c.id));
const fixtureIds=new Set(acceptance.fixtures.map(f=>f.id));
for(const entry of [...requirements.sections,...requirements.requirements,...dictionary.fields,...dictionary.role_contracts,...(dictionary.registered_extensions || [])]){
  for(const id of entry.acceptance || [])assert(checkIds.has(id),`Unknown acceptance ID ${id}`);
  for(const id of entry.fixtures || [])assert(fixtureIds.has(id),`Unknown fixture ID ${id}`);
}

const negatives=[];
function negative(name, mutate) {
  const req=structuredClone(requirements); const dict=structuredClone(dictionary);mutate(req,dict);
  let rejected=false;try {validate(req,dict);} catch {rejected=true;}
  assert(rejected, `Negative case was not detected: ${name}`);negatives.push(name);
}
negative('missing_inventory_line',(r)=>{r.requirements.splice(10,1);});
negative('unmapped_inventory_field',(r)=>{r.requirements.find(u=>u.kind==='item'&&u.section==='BD0.1').fields=[];});
negative('unknown_storage_column',(r)=>{r.requirements.find(u=>u.status==='field_contract').fields.push('missing.column');});
negative('changed_source_wording',(r)=>{r.requirements[0].source.text+=' changed';});
negative('missing_schema_field',(_,d)=>{d.fields.pop();});
negative('wrong_scalar_type',(_,d)=>{d.fields[0].storage.sqlite_type='WRONG';});
negative('implementation_claim_in_design',(_,d)=>{d.fields[0].implementation_status='passed';});
negative('missing_knowledge_state',(_,d)=>{d.fields.find(f=>f.storage.nullable).missing_states=['known'];});
negative('missing_semantic_parameter_collection',(_,d)=>{d.collections=d.collections.filter(c=>c.id!=='collection.parameter.parameters');});
negative('wrong_singleton_cardinality',(_,d)=>{d.collections.find(c=>c.id==='collection.entity_detail').multiplicity='0..N';});
negative('role_filter_changes_child_order',(_,d)=>{d.collections.find(c=>c.table==='operation_child').ordering_contract.partition_columns.push('role');});
negative('missing_membership_context_scope',(_,d)=>{d.collections.find(c=>c.table==='group_membership').discriminator_columns=[];});
negative('required_capture_lost_in_P1',(_,d)=>{d.role_contracts.find(c=>c.role==='capture').profiles=['P2','P3','P4'];});
negative('native_attribute_spelling_unmapped',(r,d)=>{r.requirements.find(u=>u.id==='BD0.9.F004').registered_extension_refs=[];d.registered_extensions=[];});
negative('native_builtin_attribute_invents_type',(_,d)=>{d.fields.find(f=>f.id==='annotation.attribute_type_id').language_cases[0]={language:'C++',case:'builtin_attribute',physical_value:'invented-class-id',state:'known',reason:null};});
negative('identity_extension_missing',(_,d)=>{d.registered_extensions=d.registered_extensions.filter(e=>e.id!=='identity-input/1');});
negative('identity_preimage_leaks_through_anchor',(_,d)=>{d.registered_extensions.find(e=>e.id==='identity-input/1').profile_policy.minimal_anchor='retain';});
negative('identity_component_recipe_drift',(_,d)=>{d.registered_extensions.find(e=>e.id==='identity-input/1').domain_variants[0].component_schema.additionalProperties=true;});
negative('snapshot_registry_state_owner_missing',(_,d)=>{delete d.tables.find(t=>t.table==='snapshot').registry_pair;});

console.log(JSON.stringify({status:'passed_design_validation',...counts,contract_links:targets.size,acceptance_ids:checkIds.size,fixture_ids:fixtureIds.size,negative_cases_rejected:negatives,sqlite_version:ddl.sqlite_version,scope:'Source accounting, concrete schema mapping, field contracts and links only. No extractor, semantic fixture, build or AI benchmark success is claimed.'},null,2));
