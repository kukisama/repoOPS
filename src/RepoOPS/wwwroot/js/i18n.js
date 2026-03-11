// ===== RepoOPS Internationalization (i18n) =====

const I18n = (() => {
    const translations = {
        'en': {
            // Header
            'app.title': 'Elementary Math',
            'app.subtitle': '1+1 and 1+2 Beginner Trainer',
            'status.connected': '● Connected',
            'status.disconnected': '● Disconnected',

            // Task Panel
            'panel.tasks': '📘 Lessons',
            'panel.output': '📺 Guidance',
            'btn.refresh': 'Refresh lesson list',
            'btn.clear': 'Clear output',
            'btn.closeAll': 'Close all tasks',
            'btn.stop': 'Stop current task',
            'btn.settings': 'Edit task configuration',
            'running.label': 'Running:',
            'menu.editTask': '✏️ Edit',
            'menu.copyTask': '📋 Copy',
            'menu.deleteTask': '🗑️ Delete',
            'menu.confirmDeleteTask': 'Are you sure you want to delete this task?',
            'menu.saveConfigFailed': 'Failed to save task list changes.',
            'loading.tasks': 'Loading lessons...',
            'loading.failed': 'Failed to load lessons. Check tasks.json configuration.',
            'no.tasks': 'No lessons configured. Click ⚙️ to add items.',
            'tab.placeholder': 'Select a lesson or homework item',

            // Welcome Screen
            'welcome.title': 'Welcome to Elementary Math',
            'welcome.desc1': 'Select a lesson or homework item from the left panel to begin.',
            'welcome.desc2': 'Explanations and reference answers will appear here in real-time.',
            'welcome.tips': 'Tips:',
            'welcome.tip1': 'Click a lesson to open the explanation',
            'welcome.tip2': 'Use practice items for repeated review',
            'welcome.tip3': 'Open the homework task for after-class exercises',
            'welcome.tip4': 'Click ⚙️ to edit lesson configuration if needed',

            // Config Editor
            'editor.title': '⚙️ Task Configuration',
            'editor.global': 'Global Settings',
            'editor.scriptsBasePath': 'Scripts Base Path',
            'editor.scriptsBasePath.placeholder': 'e.g. scripts (relative to exe)',
            'editor.defaultWorkingDir': 'Default Working Directory',
            'editor.defaultWorkingDir.placeholder': 'e.g. C:\\Projects (optional)',
            'editor.groups': 'Task Groups',
            'editor.addGroup': '+ Add Group',
            'editor.groupName': 'Group Name',
            'editor.groupName.placeholder': 'e.g. Build Tasks',
            'editor.groupIcon': 'Icon',
            'editor.groupIcon.placeholder': 'e.g. 📦',
            'editor.tasks': 'Tasks',
            'editor.addTask': '+ Add Task',
            'editor.taskId': 'Task ID',
            'editor.taskId.placeholder': 'e.g. build-android',
            'editor.taskName': 'Task Name',
            'editor.taskName.placeholder': 'e.g. Build Android APK',
            'editor.taskDesc': 'Description',
            'editor.taskDesc.placeholder': 'e.g. Build the Android APK',
            'editor.taskScript': 'Script Path',
            'editor.taskScript.placeholder': 'e.g. examples/build-android.ps1',
            'editor.taskArgs': 'Arguments',
            'editor.taskArgs.placeholder': 'e.g. -ProjectName Alpha',
            'editor.taskWorkDir': 'Working Directory',
            'editor.taskWorkDir.placeholder': 'Optional override',
            'editor.taskIcon': 'Icon',
            'editor.taskIcon.placeholder': 'e.g. 🔨',
            'editor.chooseIcon': 'Choose icon',
            'editor.iconPickerTitle': 'Icon library',
            'editor.iconSearchPlaceholder': 'Search icons...',
            'editor.iconRecent': 'Recent',
            'editor.iconCategoryCommon': 'Common',
            'editor.iconCategoryBuild': 'Build',
            'editor.iconCategoryRun': 'Run',
            'editor.iconCategoryProject': 'Project',
            'editor.iconCategoryDevice': 'Device',
            'editor.iconCategoryNetwork': 'Network',
            'editor.iconNoResults': 'No matching icons',
            'editor.deleteGroup': 'Delete this group',
            'editor.deleteTask': 'Delete this task',
            'editor.save': 'Save',
            'editor.cancel': 'Cancel',
            'editor.saved': 'Configuration saved successfully!',
            'editor.saveFailed': 'Failed to save configuration.',
            'editor.confirmDeleteGroup': 'Are you sure you want to delete this group and all its tasks?',
            'editor.confirmDeleteTask': 'Are you sure you want to delete this task?',
            'editor.validationGroupName': 'Group name cannot be empty.',
            'editor.validationTaskFields': 'Task ID, Name, and Script Path are required.',
            'editor.context.copy': '📋 Copy task',
            'editor.context.delete': '🗑️ Delete task',

            // Alerts
            'alert.notConnected': 'Not connected to server. Please wait for reconnection.',
            'alert.startFailed': 'Failed to start task:',

            // Language
            'lang.switch': '中文',

            // Theme
            'theme.switchToDark': '🌙 Dark',
            'theme.switchToLight': '☀️ Light',
            'theme.switchTitleToDark': 'Switch to dark theme',
            'theme.switchTitleToLight': 'Switch to light theme'
        },
        'zh': {
            // Header
            'app.title': '数学小练习',
            'app.subtitle': '1+1 和 1+2 初级训练',
            'status.connected': '● 已连接',
            'status.disconnected': '● 未连接',

            // Task Panel
            'panel.tasks': '📘 练习',
            'panel.output': '📺 讲解',
            'btn.refresh': '刷新练习列表',
            'btn.clear': '清除输出',
            'btn.closeAll': '关闭所有任务窗口',
            'btn.stop': '停止当前任务',
            'btn.settings': '编辑任务配置',
            'running.label': '运行中：',
            'menu.editTask': '✏️ 编辑',
            'menu.copyTask': '📋 复制',
            'menu.deleteTask': '🗑️ 删除',
            'menu.confirmDeleteTask': '确定要删除此任务吗？',
            'menu.saveConfigFailed': '保存任务列表修改失败。',
            'loading.tasks': '正在加载练习...',
            'loading.failed': '加载练习失败，请检查 tasks.json 配置。',
            'no.tasks': '暂无练习配置，点击 ⚙️ 添加内容。',
            'tab.placeholder': '选择一个讲解或作业项目',

            // Welcome Screen
            'welcome.title': '欢迎来到数学小练习',
            'welcome.desc1': '从左侧面板选择 1+1、1+2 讲解或课后作业。',
            'welcome.desc2': '讲解内容和参考答案会实时显示在这里。',
            'welcome.tips': '提示：',
            'welcome.tip1': '点击任意练习即可开始学习',
            'welcome.tip2': '先看讲解，再做练习效果更好',
            'welcome.tip3': '课后作业可以重复打开用于复习',
            'welcome.tip4': '点击练习面板中的 ⚙️ 可以调整内容',

            // Config Editor
            'editor.title': '⚙️ 任务配置',
            'editor.global': '全局设置',
            'editor.scriptsBasePath': '脚本基础路径',
            'editor.scriptsBasePath.placeholder': '例如 scripts（相对于程序目录）',
            'editor.defaultWorkingDir': '默认工作目录',
            'editor.defaultWorkingDir.placeholder': '例如 C:\\Projects（可选）',
            'editor.groups': '任务分组',
            'editor.addGroup': '+ 添加分组',
            'editor.groupName': '分组名称',
            'editor.groupName.placeholder': '例如 构建任务',
            'editor.groupIcon': '图标',
            'editor.groupIcon.placeholder': '例如 📦',
            'editor.tasks': '任务列表',
            'editor.addTask': '+ 添加任务',
            'editor.taskId': '任务ID',
            'editor.taskId.placeholder': '例如 build-android',
            'editor.taskName': '任务名称',
            'editor.taskName.placeholder': '例如 构建 Android APK',
            'editor.taskDesc': '描述',
            'editor.taskDesc.placeholder': '例如 构建 Android APK 安装包',
            'editor.taskScript': '脚本路径',
            'editor.taskScript.placeholder': '例如 examples/build-android.ps1',
            'editor.taskArgs': '参数',
            'editor.taskArgs.placeholder': '例如 -ProjectName Alpha',
            'editor.taskWorkDir': '工作目录',
            'editor.taskWorkDir.placeholder': '可选，覆盖默认工作目录',
            'editor.taskIcon': '图标',
            'editor.taskIcon.placeholder': '例如 🔨',
            'editor.chooseIcon': '选择图标',
            'editor.iconPickerTitle': '图标库',
            'editor.iconSearchPlaceholder': '搜索图标...',
            'editor.iconRecent': '最近使用',
            'editor.iconCategoryCommon': '常用',
            'editor.iconCategoryBuild': '构建',
            'editor.iconCategoryRun': '运行',
            'editor.iconCategoryProject': '项目',
            'editor.iconCategoryDevice': '设备',
            'editor.iconCategoryNetwork': '网络',
            'editor.iconNoResults': '没有匹配的图标',
            'editor.deleteGroup': '删除此分组',
            'editor.deleteTask': '删除此任务',
            'editor.save': '保存',
            'editor.cancel': '取消',
            'editor.saved': '配置保存成功！',
            'editor.saveFailed': '保存配置失败。',
            'editor.confirmDeleteGroup': '确定要删除此分组及其所有任务吗？',
            'editor.confirmDeleteTask': '确定要删除此任务吗？',
            'editor.validationGroupName': '分组名称不能为空。',
            'editor.validationTaskFields': '任务ID、任务名称和脚本路径为必填项。',
            'editor.context.copy': '📋 复制任务',
            'editor.context.delete': '🗑️ 删除任务',

            // Alerts
            'alert.notConnected': '未连接到服务器，请等待重新连接。',
            'alert.startFailed': '启动任务失败：',

            // Language
            'lang.switch': 'EN',

            // Theme
            'theme.switchToDark': '🌙 深色',
            'theme.switchToLight': '☀️ 明亮',
            'theme.switchTitleToDark': '切换到深色主题',
            'theme.switchTitleToLight': '切换到明亮主题'
        }
    };

    let currentLang = 'en';

    function detectLanguage() {
        // Check localStorage first (validate against known languages)
        const saved = localStorage.getItem('repoops-lang');
        if (saved === 'en' || saved === 'zh') {
            return saved;
        }
        // Detect from browser
        const browserLang = (navigator.language || navigator.userLanguage || 'en').toLowerCase();
        if (browserLang.startsWith('zh')) {
            return 'zh';
        }
        return 'en';
    }

    function init() {
        currentLang = detectLanguage();
        applyTranslations();
    }

    function setLanguage(lang) {
        if (translations[lang]) {
            currentLang = lang;
            localStorage.setItem('repoops-lang', lang);
            applyTranslations();
        }
    }

    function toggleLanguage() {
        setLanguage(currentLang === 'en' ? 'zh' : 'en');
    }

    function t(key) {
        return (translations[currentLang] && translations[currentLang][key]) || key;
    }

    function getLang() {
        return currentLang;
    }

    function applyTranslations() {
        // Update all elements with data-i18n attribute
        document.querySelectorAll('[data-i18n]').forEach(el => {
            el.textContent = t(el.getAttribute('data-i18n'));
        });
        // Update all elements with data-i18n-title attribute
        document.querySelectorAll('[data-i18n-title]').forEach(el => {
            el.title = t(el.getAttribute('data-i18n-title'));
        });
        // Update all elements with data-i18n-placeholder attribute
        document.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
            el.placeholder = t(el.getAttribute('data-i18n-placeholder'));
        });
        // Update connection status
        const statusEl = document.getElementById('connectionStatus');
        if (statusEl) {
            const isConnected = statusEl.classList.contains('status-connected');
            statusEl.textContent = t(isConnected ? 'status.connected' : 'status.disconnected');
        }
        // Update language switch button
        const langBtn = document.getElementById('btnLangSwitch');
        if (langBtn) {
            langBtn.textContent = t('lang.switch');
        }

        if (window.Theme && typeof window.Theme.updateToggleButton === 'function') {
            window.Theme.updateToggleButton();
        }
    }

    return { init, t, setLanguage, toggleLanguage, getLang, applyTranslations };
})();
