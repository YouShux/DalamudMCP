import * as packets from '@ffcafe/elpis-network';
import { getFields, getChildren, getEnums, getStructIf, FieldType } from '@ffcafe/elpis-struct';

const shapes = new Map();
const exported = new Map(Object.entries(packets)
    .filter(([name, value]) => typeof value === 'function')
    .sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)
    .map(([name, value]) => [value, name]));

function visit(type, hint) {
    if (shapes.has(type)) return shapes.get(type).id;
    const id = exported.get(type) ?? hint;
    if ([...shapes.values()].some(shape => shape.id === id)) throw Error('Duplicate structure id ' + id);
    const row = {
        id,
        name: type.name,
        size: type.byteLength,
        condition: getStructIf(type),
        enums: getEnums(type) ?? [],
        fields: [],
    };
    shapes.set(type, row);
    const children = getChildren(type.prototype) ?? {};
    for (const [name, spec] of Object.entries(getFields(type.prototype) ?? {})) {
        const child = children[name];
        row.fields.push({
            name,
            ...spec,
            type: FieldType[spec.type],
            child: typeof child === 'function' ? visit(child, id + '.' + name) : child,
        });
    }
    return id;
}

const mapping = Object.entries(packets.PacketMap)
    .sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0)
    .map(([name, type]) => ({ name, structure: visit(type, name) }));
process.stdout.write(JSON.stringify({ packets: mapping, structures: [...shapes.values()] }, (key, value) => {
    if (typeof value === 'function') throw Error('Executable metadata: ' + key);
    if (typeof value === 'bigint') return value.toString();
    return value;
}, 2));
