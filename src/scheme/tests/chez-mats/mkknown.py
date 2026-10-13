# 从运行器的 -v 日志生成 known.ss（iron-072）：按规则给每条 FAIL 子句定原因；未命中的打印到 stderr 人工处理。
#   cd src/scheme && for f in tests/chez-mats/*.ms; do
#     dotnet ../Cleat.Stage0/bin/Release/net10.0/stage0.dll -I lib -I nanopass --boot ../boot/cleat.scheme.boot.dll tests/chez-mats/run.sps -v $(basename $f) > 日志目录/$(basename $f .ms).txt
#   done
#   python3 tests/chez-mats/mkknown.py 日志目录 > tests/chez-mats/known.ss
# 先把 known.ss 清空再跑，否则已登记的子句不会运行、也就不会出现在日志里。
# 修了 cleat 的缺陷后重新生成：对应子句不再 FAIL，自然从 known.ss 消失。
import re,sys,os,collections
LOGDIR=sys.argv[1]
entries=[]   # (file, mat, clause, why, text)
for fn in sorted(os.listdir(LOGDIR)):
    if not fn.endswith('.txt'): continue
    lines=open(os.path.join(LOGDIR,fn),encoding='utf-8',errors='replace').read().split('\n')
    for i,l in enumerate(lines):
        m=re.match(r'FAIL (\S+?\.ms):(.*):(\d+) \(line \d+\): (.*)',l)
        if not m: continue
        text=lines[i+1].strip() if i+1<len(lines) and lines[i+1].startswith('     ') else ''
        entries.append((m.group(1),m.group(2),int(m.group(3)),m.group(4),text))

def R(cat,why): return f"{cat}: {why}"
CHEZ='chez-ext'; HANG='hang'; LAX='cleat-lax'; BUG='cleat-bug'; FIX='fixnum-width'
rules=[
 (lambda f,m,c,w,t: w=='timeout', R(HANG,'循环表上的遍历，Chez 检测环后报错；R6RS 不要求终止')),
 (lambda f,m,c,w,t: "'replace" in t or 'with-output-to-file' in t, R(CHEZ,"with-output-to-file 的 'replace 选项、expand/optimize 等 Chez 工具链形式")),
 (lambda f,m,c,w,t: 'open-file-input-port "testfile' in t or 'fasl-read' in t or 'expand/optimize' in t, R(CHEZ,'读回 Chez 工具链写出的 testfile.ss')),
 (lambda f,m,c,w,t: '$primitive' in t or "#%" in t or '#2%' in t, R(CHEZ,'$primitive / #% 记法')),
 (lambda f,m,c,w,t: re.search(r'\(\(?(?:char|string|char-ci|string-ci|fl|fx|fx|<|>|<=|>=|=)[<>=]*\?? [^ ()]+\)$',t) and 'incorrect number of arguments (1)' in w, R(CHEZ,'单参数比较（R6RS 至少两个参数）')),
 (lambda f,m,c,w,t: re.match(r'\(eqv\? \(fx[+*-]',t) and 'incorrect number of arguments' in w, R(CHEZ,'fx+ / fx- / fx* 的 0、1 或 3 个参数（R6RS 为二元）')),
 (lambda f,m,c,w,t: 'hashtable-set!' in t and 'incorrect number of arguments (2)' in w or 'hashtable-ref' in t and 'incorrect number of arguments (2)' in w, R(CHEZ,'两参数的 hashtable-set! / hashtable-ref')),
 (lambda f,m,c,w,t: 'make-flvector' in t or '(eval `' in t or '(eval \'' in t and 'incorrect number' in w, R(CHEZ,'make-flvector / 单参数 eval')),
 (lambda f,m,c,w,t: 'gensym' in t, R(CHEZ,'Chez 的 gensym 语义（打印名、唯一名、两参数）')),
 (lambda f,m,c,w,t: re.search(r"\(eq\? \((?:vector|vector-copy|vector-append)[^)]*\)+ '#\(\)\)|\(eq\? \(string\) \"\"\)|\(eqv\? \(make-string[^\"]*\"\"\)|\(eqv\? \(make-string \(- 4 4\)\) \(string\)\)|\(eqv\? \(make-vector \(- 4 4\)[^)]*\) \(vector\)\)|\(eq\? \(make-bytevector 0\) #vu8\(\)\)",t), R(CHEZ,'空向量 / 空串 / 空字节向量是唯一对象')),
 (lambda f,m,c,w,t: m in ('vector-append','make-vector','vector','vector-copy') and ("'#()" in t or '(vector))' in t), R(CHEZ,'空向量是唯一对象')),
 (lambda f,m,c,w,t: "(case '(1 2 3)" in t, R(CHEZ,'case 用 equal? 比较键（R6RS 为 eqv?）')),
 (lambda f,m,c,w,t: m=='vector-copy' and 'incorrect number of arguments (3)' in w, R(CHEZ,'三参数 vector-copy（R7RS 扩展）')),
 (lambda f,m,c,w,t: m=='vector-cas!', R(CHEZ,'vector-cas! 是 Chez 扩展，前置子句跳过后本子句无意义')),
 (lambda f,m,c,w,t: m=='cond' and '=>' in t, R(BUG,'cond 的 => 接收者是宏关键字时应报语法错误（展开器把它当过程调用展开）')),
 (lambda f,m,c,w,t: m=='cpvalid', R(LAX,'letrec / letrec* 初始化前引用或赋值未检测（Chez 的 cpvalid pass），R6RS 只说"应当"报错')),
 (lambda f,m,c,w,t: m=='define-values' and '$dv-x $dv-x' in t or '$dv-x . $dv-x' in t, R(BUG,'define-values 的重复标识符未报语法错误')),
 (lambda f,m,c,w,t: m=='eqv?' and '3.0+0.0i' in t or m=='equal?' and '3.0+0.0i' in t, R(BUG,'eqv?/equal? 区分复数的 ±0.0（已修，待确认）')),
 (lambda f,m,c,w,t: f=='5_3.ms' and m=='string->number' and re.search(r'#\d+[rR]|#i#\d+r',t), R(CHEZ,'#12r 等非 2/8/10/16 进制前缀')),
 (lambda f,m,c,w,t: f=='ieee.ms' and m=='string->number', R(CHEZ,'#b 进制带指数的记法（R6RS 只有十进制有指数）')),
 (lambda f,m,c,w,t: f=='ieee.ms' and m=='fp-output', R(BUG,'number->string 1e23 的写法（已修，待确认）')),
 (lambda f,m,c,w,t: re.search(r"\(eqv\? \(/ 0 [^)]*\) 0\)",t), R(CHEZ,'(/ 0 2.0) 给精确 0（R6RS 规定 0.0）')),
 (lambda f,m,c,w,t: m=='special-cases', R(CHEZ,'(* x 0) 给精确 0（R6RS 允许 0 或不精确结果）')),
 (lambda f,m,c,w,t: re.search(r"\(error\? \(/ [-0-9.]+ 0\)\)",t), R(CHEZ,'(/ 3.5 0) 报错（R6RS 的例子 (/ 1.0 0) 是 +inf.0）')),
 (lambda f,m,c,w,t: m in ('div-and-mod','div0-and-mod0') and '$dmpairs' in t, R(CHEZ,'$dmpairs 含 0 与 0.0 除数，Chez 给 ±inf.0 / nan；R6RS 要求 x2 非零')),
 (lambda f,m,c,w,t: m=='magnitude' or m=='sqrt' and '8-3/2i' in t or m=='expt' and '2+2i' in t, R(LAX,'精确复数的 magnitude / sqrt / expt 给不精确结果（R6RS 未强制精确）')),
 (lambda f,m,c,w,t: m=='expt' and ('(expt 1.0 0)' in t or '(expt +nan.0 0)' in t), R(CHEZ,'(expt x 0) 给精确 1（cleat 随 x 不精确）')),
 (lambda f,m,c,w,t: m=='expt' and re.match(r"\(error\? \(expt 0 [^)]*i\)\)",t), R(CHEZ,'(expt 0 复数) 报错（R6RS：报错或返回未指定数，cleat 给 0.0）')),
 (lambda f,m,c,w,t: m=='angle', R(CHEZ,'正实数的 angle 给精确 0（cleat 给 0.0）')),
 (lambda f,m,c,w,t: m=='log' and '(log 5 1)' in t, R(CHEZ,'(log 1) 为精确 0 使 (log 5 1) 报错（cleat 给 +inf.0）')),
 (lambda f,m,c,w,t: m in ('asin','acos'), R(LAX,'无穷处反三角函数虚部的符号（分支切割精度）')),
 (lambda f,m,c,w,t: m=='atan' and '-1i' in t, R(LAX,'(atan -1i) 的极点未报错')),
 (lambda f,m,c,w,t: m in ('remainder','modulo') and re.search(r'\(eqv\? \((?:remainder|modulo) 0 2\.0\) 0\)',t), R(CHEZ,'(remainder 0 2.0) 给精确 0（R6RS 不精确传染）')),
 (lambda f,m,c,w,t: m in ('fxdiv-and-mod','fxdiv0-and-mod0','fxdiv','fxmod') and 'most-' in t, R(FIX,'写死 61 位 fixnum 边界')),
 (lambda f,m,c,w,t: f=='hash.ms' and m.endswith('eq-hashtable') and 'hashtable-size' in t, R(CHEZ,'弱表 / ephemeron 表配合 collect（Chez 的 GC 接口）')),
 (lambda f,m,c,w,t: f=='hash.ms' and m=='hash-functions' and 'Stra' in t, R(BUG,'string-ci-hash / string-ci=? 未做全折叠（ß → ss）')),
 (lambda f,m,c,w,t: f=='enum.ms' and m=='enumeration' and 'make-enumeration' in t and 'enum-set-intersection' in t, R(CHEZ,'全集相同的两次 make-enumeration 在 cleat 中是同一类型（生成性未规定）')),
 (lambda f,m,c,w,t: f=='enum.ms' and 'define-enumeration foo (a b c) f' in t, R(CHEZ,'全集相同的 define-enumeration 在 cleat 中是同一类型（生成性未规定）')),
 (lambda f,m,c,w,t: f=='5_4.ms' and m=='r6rs-unicode-tests' and 'x00AA' in t, R(BUG,'char-lower-case? 按 Lowercase 属性（已修，待确认）')),
 # —— 第二轮（iron-072 续）——
 (lambda f,m,c,w,t: f=='5_2.ms' and m in ('iota','list-head'), R(LAX,'iota / list-head 是 Chez 扩展（SRFI-1 风格），cleat 的版本不检查参数类型与范围')),
 (lambda f,m,c,w,t: 'not a valid radix' in w, R(CHEZ,'string->number / number->string 的任意进制（R6RS 只有 2 8 10 16）')),
 (lambda f,m,c,w,t: m=='/' and ('e300' in t or 'e-300' in t or re.search(r'\d{20,}', t)), R(LAX,'虚部为 0.0 的不精确复数折叠成 flonum（R6RS：(real? 0.25+0.0i) 为 #f）')),
 (lambda f,m,c,w,t: f=='5_3.ms' and m=='string->number' and re.search(r'"[^"]*[\d.]#', t), R(CHEZ,'R5RS 的 # 数字占位（"98##"、"1/0#"），R6RS 已删除')),
 (lambda f,m,c,w,t: f=='5_3.ms' and m=='string->number' and re.search(r'"[^"]*/[^"]*[esfdlESFDL][+-]?\d', t), R(CHEZ,'分数带指数（"2/1E3"），R6RS 的指数只接在十进制小数后')),
 (lambda f,m,c,w,t: f=='5_3.ms' and m=='string->number' and re.search(r'"#[ie]?#?[ie]?[+-]?\d+/0+"', t), R(CHEZ,'"#i1/0" 为 +inf.0（R6RS 的 (string->number "0/0") 是 #f）')),
 (lambda f,m,c,w,t: f=='5_3.ms' and m=='string->number' and '#b' in t and re.search(r'e-?\d', t), R(CHEZ,'#b 进制带指数的记法（R6RS 只有十进制有指数）')),
 (lambda f,m,c,w,t: f=='5_3.ms' and m=='string->number' and ('"0@1"' in t or '"#e1@1"' in t), R(CHEZ,'极坐标：幅值精确 0 给精确 0、#e 配超越角度给 #f（R6RS 未规定）')),
 (lambda f,m,c,w,t: f=='5_3.ms' and m=='string->number' and 'make-string 8000' in t, R(CHEZ,'8000 位数字串含 # 占位或 @ 极坐标的组合用例')),
 (lambda f,m,c,w,t: m=='number->string' and re.search(r'\(number->string [^"]* (?:16|4)\) "', t), R(CHEZ,'bignum 的十六进制大写字母 / 4 进制（R6RS 未规定大小写、基数只有 2 8 10 16）')),
 (lambda f,m,c,w,t: m=='/' and ('+0.0i' in t or '0.0+0.0i' in t), R(LAX,'虚部为 0.0 的不精确复数折叠成 flonum（R6RS：(real? 0.25+0.0i) 为 #f）')),
 (lambda f,m,c,w,t: m=='lcm' and re.search(r'\(lcm [^()]*\) 0\)', t), R(CHEZ,'(lcm x 0) 带不精确参数时 Chez 10.5 给精确 0（cleat 与 (* 1.5 0) 一致给 0.0；R6RS 未规定）')),
 (lambda f,m,c,w,t: m=='lcm' and '$lcm-result' in t, R(CHEZ,'$lcm-result 是 Chez 内部过程')),
 (lambda f,m,c,w,t: m=='expt' and re.search(r'\(eqv\? \(expt [^ ]+ 0(?:\.0)?\) 1\)|\(eqv\? \(expt 1 [^ ]+\) 1\)|\(eqv\? \(expt 0 [^ ]+\) 0\)', t), R(CHEZ,'(expt z 0)、(expt 1 z)、(expt 0 z) 给精确结果（R6RS 允许不精确）')),
 (lambda f,m,c,w,t: m=='expt' and '(error? (expt 0 -1.0))' in t, R(CHEZ,'(expt 0 -1.0) 报错（R6RS：报错或返回未指定数，cleat 按 IEEE 给 +inf.0）')),
 (lambda f,m,c,w,t: m=='expt' and re.search(r'\(expt -?\d+ 1/2\)', t), R(LAX,'精确有理数幂的精确结果（(expt 9 1/2) = 3；R6RS 允许不精确）')),
]
known=[]; unknown=[]
for e in entries:
    for pred,reason in rules:
        try:
            if pred(*e): known.append((e,reason)); break
        except Exception as ex: pass
    else: unknown.append(e)
by=collections.Counter(r.split(':')[0] for _,r in known)
print(';;; 已知差异（skeg iron-072；cleat-077 起按 cleat 的结果生成）：(文件 mat名字 子句号 原因)；子句号为 * 表示整个 mat。运行器不运行这些子句，计入 known。')
print(';;; 原因分类：')
print(';;;   chez-ext       依赖 Chez 对 R6RS 的扩展或 Chez 特有行为（R6RS 未规定，或规定为"应当报错"而 Chez 给出结果）')
print(';;;   hang           cleat 上不终止（R6RS 未要求终止的用法）；运行器会按超时处理，登记只是省去重启')
print(';;;   fixnum-width   cleat 的 fixnum 是 32 位，Chez 64 位机器上是 61 位')
print(';;;   cleat-lax      cleat 少做的检查或精度（R6RS 未强制）')
print(';;;   cleat-bug      cleat 的缺陷，待修')
print(';;; 本文件由 mkknown.py 从对拍日志生成（规则在该脚本里），手工增删请保持格式。')
cur=None
for (f,m,c,w,t),reason in known:
    if f!=cur: print(f'\n;; —— {f} ——'); cur=f
    name = m if re.match(r'^[A-Za-z0-9!$%&*/:<=>?^_~+\-.@]+$',m) and not re.match(r'^[+\-.0-9]',m) else '|'+m+'|'
    esc=reason.replace('\\','\\\\').replace('"','\\"')   # 原因里的引号按 Scheme 字符串转义
    print(f'("{f}" {name} {c} "{esc}")')
sys.stderr.write(f'known {len(known)} {dict(by)}; unknown {len(unknown)}\n')
for e in unknown: sys.stderr.write(f'  {e[0]}:{e[1]}:{e[2]} [{e[3][:50]}] {e[4][:120]}\n')
