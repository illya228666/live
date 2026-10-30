shader_type canvas_item;
uniform vec4 tint : source_color = vec4(1.0);
uniform float pulse_strength : hint_range(0.0, 1.0) = 0.0;
void fragment() {
    vec4 base = texture(TEXTURE, UV) * COLOR * tint;
    base.rgb *= 1.0 + pulse_strength * (0.5 + 0.5 * sin(TIME * 3.0));
    COLOR = base;
}
