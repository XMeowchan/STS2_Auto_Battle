extends SceneTree

const MOD_ID := "CombatAutoHost"
const PACK_ROOT := "res://pack_assets/%s" % MOD_ID

func _initialize() -> void:
    var args := OS.get_cmdline_user_args()
    if args.is_empty():
        push_error("Missing output .pck path.")
        quit(1)
        return

    var output_path := args[0]
    var packer := PCKPacker.new()
    var err := packer.pck_start(output_path)
    if err != OK:
        push_error("Failed to start pck build: %s" % err)
        quit(err)
        return

    err = packer.add_file("res://mod_manifest.json", ProjectSettings.globalize_path("res://mod_manifest.json"))
    if err != OK:
        push_error("Failed to add mod manifest: %s" % err)
        quit(err)
        return

    _collect_files(ProjectSettings.globalize_path(PACK_ROOT), "res://%s" % MOD_ID, packer)

    err = packer.flush()
    if err != OK:
        push_error("Failed to finalize pck: %s" % err)
        quit(err)
        return

    print("Built pck: %s" % output_path)
    quit(0)

func _collect_files(source_dir: String, target_dir: String, packer: PCKPacker) -> void:
    var dir := DirAccess.open(source_dir)
    if dir == null:
        push_error("Missing pack source dir: %s" % source_dir)
        quit(2)
        return

    dir.list_dir_begin()
    while true:
        var name := dir.get_next()
        if name == "":
            break
        if name.begins_with("."):
            continue

        var source_path := source_dir.path_join(name)
        var target_path := target_dir.path_join(name)
        if dir.current_is_dir():
            _collect_files(source_path, target_path, packer)
            continue

        var err := packer.add_file(target_path, source_path)
        if err != OK:
            push_error("Failed to add %s -> %s (%s)" % [source_path, target_path, err])
            quit(err)
            return

    dir.list_dir_end()
