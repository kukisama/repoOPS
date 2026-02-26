// ===== RepoOPS Internationalization (i18n) =====

const I18n = (() => {
    const translations = {
        'en': {
            // Header
            'app.title': 'RepoOPS',
            'app.subtitle': 'Script Task Runner',
            'status.connected': '● Connected',
            'status.disconnected': '● Disconnected',

            // Task Panel
            'panel.tasks': '📋 Tasks',
            'panel.output': '📺 Output',
            'btn.refresh': 'Refresh task list',
            'btn.clear': 'Clear output',
            'btn.stop': 'Stop current task',
            'btn.settings': 'Edit task configuration',
            'running.label': 'Running:',
            'loading.tasks': 'Loading tasks...',
            'loading.failed': 'Failed to load tasks. Check tasks.json configuration.',
            'no.tasks': 'No tasks configured. Click ⚙️ to add tasks.',
            'tab.placeholder': 'Select a task to run',

            // Welcome Screen
            'welcome.title': 'Welcome to RepoOPS',
            'welcome.desc1': 'Select a task from the left panel to start execution.',
            'welcome.desc2': 'Output will appear here in real-time.',
            'welcome.tips': 'Tips:',
            'welcome.tip1': 'Click any task to start it',
            'welcome.tip2': 'Multiple tasks can run simultaneously',
            'welcome.tip3': 'Click the ⏹️ button or tab close to stop a running task',
            'welcome.tip4': 'Click ⚙️ in the task panel to configure your tasks',

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

            // Alerts
            'alert.notConnected': 'Not connected to server. Please wait for reconnection.',
            'alert.startFailed': 'Failed to start task:',

            // Language
            'lang.switch': '中文'
        },
        'zh': {
            // Header
            'app.title': 'RepoOPS',
            'app.subtitle': '脚本任务运行器',
            'status.connected': '● 已连接',
            'status.disconnected': '● 未连接',

            // Task Panel
            'panel.tasks': '📋 任务',
            'panel.output': '📺 输出',
            'btn.refresh': '刷新任务列表',
            'btn.clear': '清除输出',
            'btn.stop': '停止当前任务',
            'btn.settings': '编辑任务配置',
            'running.label': '运行中：',
            'loading.tasks': '正在加载任务...',
            'loading.failed': '加载任务失败，请检查 tasks.json 配置。',
            'no.tasks': '暂无任务配置，点击 ⚙️ 添加任务。',
            'tab.placeholder': '选择一个任务来运行',

            // Welcome Screen
            'welcome.title': '欢迎使用 RepoOPS',
            'welcome.desc1': '从左侧面板选择一个任务开始执行。',
            'welcome.desc2': '输出将实时显示在这里。',
            'welcome.tips': '提示：',
            'welcome.tip1': '点击任意任务即可启动',
            'welcome.tip2': '支持多个任务同时运行',
            'welcome.tip3': '点击 ⏹️ 按钮或关闭标签页可停止运行中的任务',
            'welcome.tip4': '点击任务面板中的 ⚙️ 配置你的任务',

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

            // Alerts
            'alert.notConnected': '未连接到服务器，请等待重新连接。',
            'alert.startFailed': '启动任务失败：',

            // Language
            'lang.switch': 'EN'
        }
    };

    let currentLang = 'en';

    function detectLanguage() {
        const saved = localStorage.getItem('repoops-lang');
        if (saved === 'en' || saved === 'zh') {
            return saved;
        }
        const browserLang = (navigator.language || navigator.userLanguage || 'en').toLowerCase();
        return browserLang.startsWith('zh') ? 'zh' : 'en';
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
        document.querySelectorAll('[data-i18n]').forEach(el => {
            el.textContent = t(el.getAttribute('data-i18n'));
        });
        document.querySelectorAll('[data-i18n-title]').forEach(el => {
            el.title = t(el.getAttribute('data-i18n-title'));
        });
        document.querySelectorAll('[data-i18n-placeholder]').forEach(el => {
            el.placeholder = t(el.getAttribute('data-i18n-placeholder'));
        });

        const statusEl = document.getElementById('connectionStatus');
        if (statusEl) {
            const isConnected = statusEl.classList.contains('status-connected');
            statusEl.textContent = t(isConnected ? 'status.connected' : 'status.disconnected');
        }

        const langBtn = document.getElementById('btnLangSwitch');
        if (langBtn) {
            langBtn.textContent = t('lang.switch');
        }
    }

    return { init, t, setLanguage, toggleLanguage, getLang, applyTranslations };
})();
