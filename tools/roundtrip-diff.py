#!/usr/bin/env python3
"""Semantic diff: original OpenAPI spec vs Rivet re-emitted spec.

The corpus audit behind FABLE_ROUNDTRIP.md (2026-06-12). Compares operations
(request bodies, responses, params, security) and component schemas (props,
required, types, formats, enums, nullability, defaults) between an original
spec and its import->emit round-trip, resolving $refs on both sides.

Usage:
    roundtrip-diff.py <original.json> <reemitted.json> [--summary-json <path>] [--details-json <path>]

--summary-json writes machine-readable completeness and semantic finding
counts. Exit 0 means no findings, 1 means semantic drift, and 2 means invalid
arguments or input.
"""
import json, re, sys, collections

args = [a for a in sys.argv[1:] if not a.startswith('--')]
ORIG, REEMIT = args[0], args[1]
SUMMARY_PATH = None
if '--summary-json' in sys.argv:
    SUMMARY_PATH = sys.argv[sys.argv.index('--summary-json') + 1]
DETAILS_PATH = None
if '--details-json' in sys.argv:
    DETAILS_PATH = sys.argv[sys.argv.index('--details-json') + 1]

o = json.load(open(ORIG))
r = json.load(open(REEMIT))

def resolver(doc):
    def res(s, depth=0):
        while isinstance(s, dict) and '$ref' in s and depth < 20:
            ref = s['$ref']
            if not ref.startswith('#/'):
                return s
            cur = doc
            for part in ref[2:].split('/'):
                part = part.replace('~1', '/').replace('~0', '~')
                if isinstance(cur, dict):
                    cur = cur.get(part, {})
                elif isinstance(cur, list) and part.isdigit() and int(part) < len(cur):
                    cur = cur[int(part)]
                else:
                    cur = {}
            s = cur
            depth += 1
        return s
    return res

ores = resolver(o)
rres = resolver(r)

METHODS = ('get', 'put', 'post', 'delete', 'patch', 'head', 'options', 'trace')

def ops(doc):
    out = {}
    for p, item in doc.get('paths', {}).items():
        for m in METHODS:
            if m in item:
                # merge path-level params
                op = dict(item[m])
                pl = item.get('parameters', [])
                if pl:
                    op = dict(op)
                    op['parameters'] = list(op.get('parameters', [])) + list(pl)
                out[(p, m)] = op
    return out

oo = ops(o)
ro = ops(r)

findings = collections.defaultdict(list)

def add(cat, key, detail):
    findings[cat].append((key, detail))

def top_type(s, res):
    s = res(s)
    if not isinstance(s, dict):
        return ('?',)
    # Unwrap the nullable composition (oneOf/anyOf of [X, {type: null}]):
    # 3.0 `nullable: true` on a component and a 3.1-style null branch are the
    # same claim in different clothes — classifying them as different top types
    # flagged every nullable-$ref body after the FABLE_ROUNDTRIP #6 fix.
    for comp_key in ('oneOf', 'anyOf'):
        branches = s.get(comp_key)
        if isinstance(branches, list) and len(branches) == 2:
            non_null = [b for b in branches if not (isinstance(b, dict) and b.get('type') == 'null')]
            if len(non_null) == 1:
                s = res(non_null[0])
                if not isinstance(s, dict):
                    return ('?',)
                break
    t = s.get('type')
    if isinstance(t, list):
        t = '|'.join(sorted(x for x in t if x != 'null')) or 'null'
    f = s.get('format')
    comp = next((k for k in ('oneOf','anyOf','allOf') if k in s), None)
    return (t, f, comp, len(s.get(comp, [])) if comp else None)

# ---------- per-operation comparison ----------
shared = sorted(set(oo) & set(ro))
only_o = sorted(set(oo) - set(ro))
only_r = sorted(set(ro) - set(oo))
print(f"ops: orig={len(oo)} reemit={len(ro)} shared={len(shared)} only_orig={len(only_o)} only_reemit={len(only_r)}")

DOC_STATUS_MAP = {'default': '500', '4XX': '400', '5XX': '500', '2XX': '200'}

for key in shared:
    a, b = oo[key], ro[key]
    # --- requestBody ---
    arb, brb = a.get('requestBody'), b.get('requestBody')
    arb = ores(arb) if arb else None
    brb = rres(brb) if brb else None
    act = set((arb or {}).get('content', {}))
    bct = set((brb or {}).get('content', {}))
    if act != bct:
        add('reqbody-content-types', key, (sorted(act), sorted(bct)))
    areq = (arb or {}).get('required', False)
    breq = (brb or {}).get('required', False)
    if arb and brb and areq != breq:
        add('reqbody-required-flag', key, (areq, breq))
    if arb and not brb:
        add('reqbody-dropped', key, sorted(act))
    if brb and not arb:
        add('reqbody-invented', key, sorted(bct))
    # top-level schema type of shared content types
    for ct in act & bct:
        ta = top_type((arb['content'][ct] or {}).get('schema', {}), ores)
        tb = top_type((brb['content'][ct] or {}).get('schema', {}), rres)
        if ta[0] != tb[0]:
            add('reqbody-schema-type', key, (ct, ta, tb))
    # --- responses ---
    ares = a.get('responses', {})
    bres = b.get('responses', {})
    amapped = set()
    for s in ares:
        amapped.add(DOC_STATUS_MAP.get(s, s))
    bset = set(bres)
    # success collapse is documented (lowest 2xx wins) -> map: keep only lowest 2xx of orig
    a2xx = sorted(s for s in amapped if s.startswith('2'))
    aexp = set(amapped)
    if a2xx:
        aexp -= set(a2xx[1:])
    missing = aexp - bset
    extra = bset - aexp
    if missing:
        add('response-status-missing', key, sorted(missing))
    if extra:
        add('response-status-invented', key, sorted(extra))
    for s in set(ares) & bset:
        ac = set(ores(ares[s]).get('content', {}))
        bc = set(rres(bres[s]).get('content', {}))
        if ac != bc:
            add('response-content-types', key, (s, sorted(ac), sorted(bc)))
        for ct in ac & bc:
            ta = top_type((ores(ares[s])['content'][ct] or {}).get('schema', {}), ores)
            tb = top_type((rres(bres[s])['content'][ct] or {}).get('schema', {}), rres)
            if ta[0] != tb[0]:
                add('response-schema-type', key, (s, ct, ta, tb))
    # --- parameters ---
    def params(op, res):
        out = {}
        for p in op.get('parameters', []):
            p = res(p)
            out[(p.get('name'), p.get('in'))] = bool(p.get('required', False))
        return out
    ap = params(a, ores)
    bp = params(b, rres)
    anames = {n for n, _ in ap}
    bnames = {n for n, _ in bp}
    for (n, loc), req in ap.items():
        if (n, loc) in bp:
            if bp[(n, loc)] != req:
                add('param-required-flip', key, (n, loc, req, bp[(n, loc)]))
        elif n in bnames:
            nloc = next(l for (nn, l) in bp if nn == n)
            add('param-relocated', key, (n, loc, '->', nloc))
        else:
            add('param-dropped', key, (n, loc))
    for (n, loc) in bp:
        if n not in anames:
            add('param-invented', key, (n, loc))
    # --- security ---
    asec = a.get('security')
    bsec = b.get('security')
    def secnames(sec):
        if sec is None: return None
        return sorted({k for clause in sec for k in clause})
    if secnames(asec) != secnames(bsec):
        add('op-security', key, (secnames(asec), secnames(bsec)))

# ---------- component schema comparison ----------
def norm(s): return re.sub(r'[^a-z0-9]', '', s.lower())
# Inline-only specs (e.g. notion) have no schema section at all. Swagger 2
# stores definitions at the document root; OpenAPI 3 uses components.schemas.
def schemas(doc):
    return doc.get('components', {}).get('schemas', doc.get('definitions', {}))

o_schemas = schemas(o)
r_schemas = schemas(r)

def names_by_normalized(names):
    result = collections.defaultdict(list)
    for name in names:
        result[norm(name)].append(name)
    return result

on = names_by_normalized(o_schemas)
rn = names_by_normalized(r_schemas)
original_name_collisions = {key: names for key, names in on.items() if len(names) > 1}
reemitted_name_collisions = {key: names for key, names in rn.items() if len(names) > 1}
unmatched_original_schemas = sorted(
    name for name in o_schemas if norm(name) not in rn
)
unmatched_reemitted_schemas = sorted(
    name for name in r_schemas if norm(name) not in on
)
pairs = [
    (original_names[0], rn[key][0])
    for key, original_names in on.items()
    if key in rn and len(original_names) == 1 and len(rn[key]) == 1
]
print(
    f"schemas: orig={len(o_schemas)} reemit={len(r_schemas)} matched={len(pairs)} "
    f"only_orig={len(unmatched_original_schemas)} only_reemit={len(unmatched_reemitted_schemas)} "
    f"collisions={len(original_name_collisions) + len(reemitted_name_collisions)}"
)

def is_nullable(s):
    if s.get('nullable'): return True
    t = s.get('type')
    if isinstance(t, list) and 'null' in t: return True
    for k in ('oneOf', 'anyOf'):
        for v in s.get(k, []):
            if isinstance(v, dict) and v.get('type') == 'null': return True
    return False

def eff_type(s):
    t = s.get('type')
    if isinstance(t, list):
        tt = [x for x in t if x != 'null']
        return tt[0] if len(tt) == 1 else '|'.join(sorted(tt))
    return t

schema_findings = collections.defaultdict(list)
def sadd(cat, key, detail):
    schema_findings[cat].append((key, detail))

def flatten_allof(s, res, depth=0):
    """merge allOf chains for property comparison"""
    s = res(s)
    if 'allOf' not in s or depth > 6:
        return s
    merged = {'properties': {}, 'required': []}
    for part in s['allOf']:
        fp = flatten_allof(part, res, depth+1)
        merged['properties'].update(fp.get('properties', {}))
        merged['required'] += fp.get('required', [])
    for k, v in s.items():
        if k == 'allOf': continue
        if k == 'properties':
            merged['properties'].update(v)
        elif k == 'required':
            merged['required'] += v
        else:
            merged[k] = v
    return merged

for on, rn_ in pairs:
    osch = flatten_allof(o_schemas[on], ores)
    rsch = flatten_allof(r_schemas[rn_], rres)
    okind = eff_type(osch) or ('object' if 'properties' in osch else None)
    rkind = eff_type(rsch) or ('object' if 'properties' in rsch else None)
    # composition arity
    for comp in ('oneOf', 'anyOf'):
        oc = o_schemas[on].get(comp)
        rc = r_schemas[rn_].get(comp)
        if oc and not rc and comp not in ('anyOf',):
            pass
    if okind != rkind:
        sadd('schema-kind', (on, rn_), (okind, rkind))
        continue
    oprops = osch.get('properties', {})
    rprops = rsch.get('properties', {})
    # property name mapping: wire names should be identical now (pinning)
    od = set(oprops) - set(rprops)
    rd = set(rprops) - set(oprops)
    if od: sadd('props-dropped', (on, rn_), sorted(od)[:6])
    if rd: sadd('props-invented', (on, rn_), sorted(rd)[:6])
    oreq = set(osch.get('required', []))
    rreq = set(rsch.get('required', []))
    shared_props = set(oprops) & set(rprops)
    req_lost = (oreq - rreq) & shared_props
    req_gained = (rreq - oreq) & shared_props
    if req_lost: sadd('required-lost', (on, rn_), sorted(req_lost)[:6])
    if req_gained: sadd('required-OVERCLAIM', (on, rn_), sorted(req_gained)[:6])
    # additionalProperties
    oap = osch.get('additionalProperties')
    rap = rsch.get('additionalProperties')
    def apkind(v):
        if v is None: return 'absent'
        if v is True: return 'true'
        if v is False: return 'false'
        return 'schema'
    if apkind(oap) != apkind(rap):
        sadd(f'addprops-{apkind(oap)}->{apkind(rap)}', (on, rn_), None)
    # per-property comparison — unwrap the nullable composition on both sides
    # first (oneOf/anyOf of [X, null]); nullability itself is compared via
    # is_nullable on the WRAPPED form, but enum/format/type/default live on the
    # inner schema. Reading them off the wrapper miscounted every optional
    # enum-typed property as a dropped enum (519 false findings).
    def unwrap_nullable(s, res):
        for comp_key in ('oneOf', 'anyOf'):
            branches = s.get(comp_key)
            if isinstance(branches, list) and len(branches) == 2:
                non_null = [b for b in branches if not (isinstance(b, dict) and b.get('type') == 'null')]
                if len(non_null) == 1:
                    inner = res(non_null[0])
                    if isinstance(inner, dict):
                        return inner
        return s

    for pn in shared_props:
        ops_ = ores(oprops[pn]); rps = rres(rprops[pn])
        if not isinstance(ops_, dict) or not isinstance(rps, dict): continue
        onul_pre, rnul_pre = is_nullable(ops_), is_nullable(rps)
        ops_, rps = unwrap_nullable(ops_, ores), unwrap_nullable(rps, rres)
        ot, rt = eff_type(ops_), eff_type(rps)
        if ot != rt and not (ot is None or rt is None):
            sadd('prop-type', (on, rn_, pn), (ot, rt))
        of, rf = ops_.get('format'), rps.get('format')
        if of != rf:
            sadd(f'prop-format-{of}->{rf}', (on, rn_, pn), None)
        oe = ops_.get('enum'); re_ = rps.get('enum')
        if oe is not None or re_ is not None:
            osz = None if oe is None else sorted(map(str, [x for x in oe if x is not None]))
            rsz = None if re_ is None else sorted(map(str, [x for x in re_ if x is not None]))
            if osz != rsz:
                sadd('prop-enum', (on, rn_, pn), (oe, re_))
        onul = onul_pre or is_nullable(ops_)
        rnul = rnul_pre or is_nullable(rps)
        if onul != rnul:
            sadd(f'prop-nullable-{onul}->{rnul}', (on, rn_, pn), None)
        odef, rdef = ops_.get('default'), rps.get('default')
        if odef != rdef:
            sadd(f'prop-default-{"present" if odef is not None else "absent"}->{"present" if rdef is not None else "absent"}', (on, rn_, pn), (odef, rdef))

# ---------- report ----------
print("\n===== OPERATION-LEVEL =====")
for cat in sorted(findings, key=lambda c: -len(findings[c])):
    items = findings[cat]
    print(f"\n## {cat}: {len(items)}")
    for k, d in items[:5]:
        print("  ", k, d)

print("\n===== SCHEMA-LEVEL =====")
for cat in sorted(schema_findings, key=lambda c: -len(schema_findings[c])):
    items = schema_findings[cat]
    print(f"\n## {cat}: {len(items)}")
    for k, d in items[:5]:
        print("  ", k, d)

flagged_ops = {k for items in findings.values() for k, _ in items if isinstance(k, tuple)}
clean = len([k for k in shared if k not in flagged_ops])
print(f"\nclean ops: {clean}/{len(shared)} ({100 * clean // max(len(shared), 1)}%)")

summary = {
    "originalOps": len(oo),
    "reemittedOps": len(ro),
    "sharedOps": len(shared),
    "missingOperations": len(only_o),
    "inventedOperations": len(only_r),
    "originalSchemas": len(o_schemas),
    "reemittedSchemas": len(r_schemas),
    "matchedSchemas": len(pairs),
    "unmatchedOriginalSchemas": len(unmatched_original_schemas),
    "unmatchedReemittedSchemas": len(unmatched_reemitted_schemas),
    "originalSchemaNameCollisions": len(original_name_collisions),
    "reemittedSchemaNameCollisions": len(reemitted_name_collisions),
    "totalOps": len(shared),
    "cleanOps": clean,
    "opFindings": {cat: len(items) for cat, items in findings.items()},
    "schemaFindings": {cat: len(items) for cat, items in schema_findings.items()},
}

if SUMMARY_PATH:
    json.dump(summary, open(SUMMARY_PATH, 'w'), indent=2, sort_keys=True)

if DETAILS_PATH:
    details = {
        "missingOperations": [list(key) for key in only_o],
        "inventedOperations": [list(key) for key in only_r],
        "unmatchedOriginalSchemas": unmatched_original_schemas,
        "unmatchedReemittedSchemas": unmatched_reemitted_schemas,
        "originalSchemaNameCollisions": original_name_collisions,
        "reemittedSchemaNameCollisions": reemitted_name_collisions,
        "opFindings": {cat: [[list(k), repr(d)] for k, d in items] for cat, items in findings.items()},
        "schemaFindings": {cat: [[list(k) if isinstance(k, tuple) else k, repr(d)] for k, d in items] for cat, items in schema_findings.items()},
    }
    json.dump(details, open(DETAILS_PATH, 'w'), indent=1, sort_keys=True)

has_findings = any((
    only_o,
    only_r,
    unmatched_original_schemas,
    unmatched_reemitted_schemas,
    original_name_collisions,
    reemitted_name_collisions,
    findings,
    schema_findings,
))
sys.exit(1 if has_findings else 0)
