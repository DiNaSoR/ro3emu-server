const B=require('./bundle.js');const fs=require('fs');
const es=B.entriesFor('GameUpdateContent_en.json');const bun=B.openBundle(es[0]);const T={};
for(const f of bun.files)for(const o of f.sf.objects){if(o.type.classID!==49)continue;const v=B.U.readTree(f.sf,o);const t=Buffer.isBuffer(v.m_Script)?v.m_Script.toString('utf8'):v.m_Script;try{T[v.m_Name]=JSON.parse(t.replace(/^\uFEFF/,'')).stageinfo}catch{}}
const zh=T.GameUpdateContent,en=T.GameUpdateContent_en;const rows=['key\tvisible\ttext\texisting_en\tstage_note\tsource'];
const esc=s=>(s==null?'':String(s)).replace(/\t/g,'\t').replace(/\n/g,'\n');
zh.forEach((z,i)=>{const e=en[i];const same=e&&e.stage===z.stage;rows.push([z.key,'yes (boot/patch screen)',z.content,same?e.content:'',z.stage,'HappyEngine.Resource LauncherLanguage._compatibleDict (LauncherLanguage.cs:16-58); GameUpdateContent_en.json has key "None" for every row'].map(esc).join('\t'))});
fs.writeFileSync(require('path').join(require('path').resolve(__dirname, '../..'), 're/loc/launcher_strings.tsv'),'\ufeff'+rows.join('\n')+'\n');console.log(rows.length-1, rows.slice(1,6).join('\n'));
