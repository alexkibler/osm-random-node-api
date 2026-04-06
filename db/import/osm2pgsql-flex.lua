local valid_node_ids = {}
local valid_highway_types = {
    cycleway=true, residential=true, tertiary=true,
    path=true, track=true, living_street=true,
}

local nodes_table = osm2pgsql.define_node_table('osm_nodes', {
    { column='geom', type='point', projection=4326 },
})

function osm2pgsql.process_way(object)
    local tags = object.tags
    if not tags then return end
    if tags['access'] == 'private' then return end
    local hw = tags['highway']
    if not hw or hw == 'motorway' or hw == 'trunk' then return end
    if not valid_highway_types[hw] then return end
    for _, id in ipairs(object.nodes) do
        valid_node_ids[id] = true
    end
end

function osm2pgsql.process_node(object)
    if valid_node_ids[object.id] then
        nodes_table:insert({ geom = object:as_point() })
    end
end
